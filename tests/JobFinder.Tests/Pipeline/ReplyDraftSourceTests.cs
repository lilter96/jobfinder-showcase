using System.Reflection;
using System.Text.Json;
using JobFinder.App.Cli;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Tests.Pipeline;

public sealed partial class OrdinaryNotificationAdmissionTests
{
    private async Task<ApplicationEvent> ReplySourceAsync()
    {
        var application = await ApplicationAsync(ApplicationStatus.Sent);
        var source = new ApplicationEvent
        {
            ApplicationId = application.Id, Kind = EventKind.MessageIn, Channel = EventChannel.Email,
            Author = "synthetic@example.invalid", Text = "We will get back to you", At = DateTimeOffset.UtcNow,
            Draft = "Original draft", DraftStatus = DraftStatus.Pending, Revision = 2,
        };
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.ApplicationEvents.Add(source);
        await context.SaveChangesAsync(Ct);
        return source;
    }

    private static ReplyAnalysis ReplyAnalysis() => new()
    {
        Classification = "other", Summary = "Synthetic acknowledgement", Intent = "small_talk", Counterpart = "human",
        Draft = "Спасибо, буду ждать ответа.",
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reply_source_owner_publishes_one_captured_edit_or_rolls_back_after_native_admission(bool fail)
    {
        var source = await ReplySourceAsync();
        var host = await HostAsync();
        var owner = host.Services.GetRequiredService<ReplyDraftSources>();
        var captured = Assert.IsType<CapturedReplyDraft>(await owner.CaptureAsync(source.Id, Ct));
        _fault.Active = fail;
        if (fail) await Assert.ThrowsAsync<InvalidOperationException>(() => owner.PublishAsync(captured.Snapshot, ReplyAnalysis(), [], Ct));
        else
        {
            var results = await Task.WhenAll(owner.PublishAsync(captured.Snapshot, ReplyAnalysis(), [], Ct),
                owner.PublishAsync(captured.Snapshot, ReplyAnalysis(), [], Ct));
            Assert.Single(results, result => result.Ok);
        }
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var saved = await context.ApplicationEvents.SingleAsync(row => row.Id == source.Id, Ct);
        Assert.Equal(fail ? source.Draft : ReplyAnalysis().Draft, saved.Draft);
        Assert.Equal(source.Revision + (fail ? 0 : 1), saved.Revision);
        if (fail) await NoAdmissionAsync(context);
        else await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
        Assert.Equal(0, _provider.Calls);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("draft")]
    [InlineData("status")]
    [InlineData("text")]
    [InlineData("channel")]
    [InlineData("author")]
    [InlineData("external")]
    [InlineData("time")]
    [InlineData("classification")]
    [InlineData("kind")]
    [InlineData("vacancy")]
    [InlineData("salary")]
    [InlineData("skills")]
    [InlineData("experience")]
    [InlineData("city")]
    [InlineData("application")]
    [InlineData("conversation")]
    [InlineData("profile")]
    [InlineData("facts")]
    [InlineData("story")]
    public async Task Reply_edit_refuses_a_changed_captured_witness_without_rebasing(string changed)
    {
        var source = await ReplySourceAsync();
        var host = await HostAsync();
        var owner = host.Services.GetRequiredService<ReplyDraftSources>();
        var captured = Assert.IsType<CapturedReplyDraft>(await owner.CaptureAsync(source.Id, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var current = await context.ApplicationEvents.SingleAsync(row => row.Id == source.Id, Ct);
        var application = await context.Applications.SingleAsync(row => row.Id == current.ApplicationId, Ct);
        var vacancy = await context.Vacancies.SingleAsync(row => row.Id == application.VacancyId, Ct);
        switch (changed)
        {
            case "revision": current.Revision++; break;
            case "draft": current.Draft = "Concurrent edit"; break;
            case "status": current.DraftStatus = DraftStatus.Dismissed; break;
            case "text": current.Text = "Different question"; break;
            case "channel": current.Channel = EventChannel.Telegram; break;
            case "author": current.Author = "another@example.invalid"; break;
            case "external": current.ExternalId = "different"; break;
            case "time": current.At = current.At.AddMinutes(-1); break;
            case "classification": current.Classification = ReplyClass.Rejection; break;
            case "kind": current.Kind = EventKind.MessageOut; break;
            case "vacancy": vacancy.Description += " Changed"; break;
            case "salary": vacancy.SalaryFrom = 4500; break;
            case "skills": vacancy.Skills = ["Changed skill"]; break;
            case "experience": vacancy.Experience = "Changed experience"; break;
            case "city": vacancy.City = "Changed city"; break;
            case "application": application.Revision++; break;
            case "conversation":
                context.ApplicationEvents.Add(new ApplicationEvent
                {
                    ApplicationId = application.Id, Kind = EventKind.MessageIn, Channel = EventChannel.Email,
                    At = DateTimeOffset.UtcNow, Text = "Concurrent question",
                });
                break;
            case "profile":
                var profiles = host.Services.GetRequiredService<ProfileProvider>();
                var profile = await profiles.GetSnapshotAsync(Ct);
                await profiles.SaveAsync(new CandidateProfile { Name = "Updated synthetic candidate" }, profile?.Version ?? 0, "test", Ct);
                break;
            case "facts": await host.Services.GetRequiredService<FactsProvider>().UpsertAsync("synthetic", "Question", "Answer", Ct); break;
            case "story": context.Stories.Add(new Story { Title = "Updated synthetic story", Confirmed = true, Action = Guid.NewGuid().ToString("N") }); break;
        }
        await context.SaveChangesAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        Assert.False((await owner.PublishAsync(captured.Snapshot, ReplyAnalysis(), [], Ct)).Ok);
        var saved = await context.ApplicationEvents.AsNoTracking().SingleAsync(row => row.Id == source.Id, Ct);
        Assert.Equal(current.Draft, saved.Draft);
        Assert.Equal(current.Revision, saved.Revision);
        await NoAdmissionAsync(context);
        Assert.Equal(0, _provider.Calls);
    }

    [Theory]
    [InlineData(DraftStatus.Sent)]
    [InlineData(DraftStatus.Sending)]
    [InlineData(DraftStatus.OutcomeUnknown)]
    public async Task Reply_edit_preserves_terminal_or_unknown_history(DraftStatus status)
    {
        var source = await ReplySourceAsync();
        var host = await HostAsync();
        var owner = host.Services.GetRequiredService<ReplyDraftSources>();
        var captured = Assert.IsType<CapturedReplyDraft>(await owner.CaptureAsync(source.Id, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.ApplicationEvents.Where(row => row.Id == source.Id).ExecuteUpdateAsync(update => update.SetProperty(row => row.DraftStatus, status), Ct);
        Assert.Null(await owner.CaptureAsync(source.Id, Ct));
        Assert.False((await owner.PublishAsync(captured.Snapshot, ReplyAnalysis(), [], Ct)).Ok);
        await NoAdmissionAsync(context);
    }

    [Theory]
    [InlineData(EmployerMessageStatus.Pending)]
    [InlineData(EmployerMessageStatus.Ready)]
    [InlineData(EmployerMessageStatus.Sending)]
    [InlineData(EmployerMessageStatus.OutcomeUnknown)]
    public async Task Reply_edit_cannot_invalidate_an_admitted_employer_send(EmployerMessageStatus status)
    {
        var source = await ReplySourceAsync();
        var host = await HostAsync();
        var owner = host.Services.GetRequiredService<ReplyDraftSources>();
        var captured = Assert.IsType<CapturedReplyDraft>(await owner.CaptureAsync(source.Id, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.EmployerMessages.Add(new EmployerMessage
        {
            Id = Guid.NewGuid(), VacancyId = captured.Snapshot.VacancyId, ApplicationId = source.ApplicationId,
            BusinessKey = Guid.NewGuid().ToString("N"), InputJson = "{}", Status = status, CreatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync(Ct);
        Assert.Null(await owner.CaptureAsync(source.Id, Ct));
        Assert.False((await owner.PublishAsync(captured.Snapshot, ReplyAnalysis(), [], Ct)).Ok);
        await NoAdmissionAsync(context);
    }

    [Theory]
    [InlineData("root", false)]
    [InlineData("root", true)]
    [InlineData("attempt", false)]
    [InlineData("attempt", true)]
    [InlineData("application", false)]
    [InlineData("application", true)]
    public async Task Reply_edit_preserves_an_unlinked_initial_application_effect(string kind, bool unknown)
    {
        var source = await ReplySourceAsync();
        var host = await HostAsync();
        var owner = host.Services.GetRequiredService<ReplyDraftSources>();
        var captured = Assert.IsType<CapturedReplyDraft>(await owner.CaptureAsync(source.Id, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (kind == "root") context.ApplicationDeliveryRequests.Add(new ApplicationDeliveryRequest
        {
            Id = Guid.NewGuid(), ApplicationId = source.ApplicationId, ApplicationRevision = 999,
            Stage = unknown ? ApplicationDeliveryStage.OutcomeUnknown : ApplicationDeliveryStage.Sending,
            InputJson = "{}", InputHash = CanonicalJson.Hash("{}"), CreatedAt = DateTimeOffset.UtcNow,
        });
        if (kind == "attempt") context.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(), ApplicationId = source.ApplicationId, ApplicationRevision = 999, OperationId = Guid.NewGuid(),
            Status = unknown ? DeliveryAttemptStatus.OutcomeUnknown : DeliveryAttemptStatus.Submitting,
            Text = "Unresolved synthetic effect", TextHash = Fingerprint.Of("Unresolved synthetic effect"),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        if (kind == "application")
        {
            var application = await context.Applications.SingleAsync(row => row.Id == source.ApplicationId, Ct);
            application.Status = unknown ? ApplicationStatus.OutcomeUnknown : ApplicationStatus.Sending;
        }
        await context.SaveChangesAsync(Ct);
        Assert.Null(await owner.CaptureAsync(source.Id, Ct));
        Assert.False((await owner.PublishAsync(captured.Snapshot, ReplyAnalysis(), [], Ct)).Ok);
        await NoAdmissionAsync(context);
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("facts")]
    [InlineData("preview")]
    public async Task Actual_paid_redraft_uses_frozen_inputs_and_never_overwrites_a_concurrent_change(string changed)
    {
        var source = await ReplySourceAsync();
        var host = await HostAsync(consumer: true);
        await host.Services.GetRequiredService<FactsProvider>().GetAsync(Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        _provider.Response = JsonSerializer.Serialize(ReplyAnalysis(), CareerJsonContext.Default.ReplyAnalysis);
        _provider.BeforeResponse = async () =>
        {
            if (changed == "edit")
                await context.ApplicationEvents.Where(row => row.Id == source.Id).ExecuteUpdateAsync(update => update
                    .SetProperty(row => row.Draft, "Concurrent edit").SetProperty(row => row.Revision, row => row.Revision + 1), Ct);
            if (changed == "facts")
            {
                await host.Services.GetRequiredService<FactsProvider>().UpsertAsync("synthetic", "Question", "Concurrent fact", Ct);
                await NotificationAdmissionAssertions.ResetAsync(context, Ct);
            }
        };
        var result = await host.Services.GetRequiredService<ReplyService>().RedraftAsync(source.Id, Ct, preview: changed == "preview");
        Assert.Equal(changed == "preview", result.Ok);
        var saved = await context.ApplicationEvents.SingleAsync(row => row.Id == source.Id, Ct);
        Assert.Equal(changed == "edit" ? "Concurrent edit" : source.Draft, saved.Draft);
        Assert.Equal(source.Revision + (changed == "edit" ? 1 : 0), saved.Revision);
        if (changed == "preview") await NoAdmissionAsync(context);
        else Assert.Single(await context.Notifications.Where(row => row.Kind == NotificationKind.Alert).ToListAsync(Ct));
        Assert.Equal(1, _provider.Calls);
    }

    [Fact]
    public async Task Actual_cli_reply_card_joins_native_publication_without_paid_work()
    {
        var source = await ReplySourceAsync();
        var host = await HostAsync();
        var command = typeof(CliHost).GetMethod("ReplyCardAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var result = await (Task<int>)command.Invoke(null,
            [host.Services, source.Id.ToString(), new[] { "--draft", "Спасибо, буду ждать ответа." }, Ct])!;
        Assert.Equal(0, result);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var saved = await context.ApplicationEvents.SingleAsync(row => row.Id == source.Id, Ct);
        Assert.Equal(source.Revision + 1, saved.Revision);
        Assert.Equal("Спасибо, буду ждать ответа.", saved.Draft);
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
        Assert.Equal(0, _provider.Calls);
    }
}
