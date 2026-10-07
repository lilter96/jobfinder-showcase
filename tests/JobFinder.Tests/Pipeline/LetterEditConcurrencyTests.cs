using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using JobFinder.Bot;
using JobFinder.Core.Domain;
using JobFinder.Pipeline.Actions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableApplicationDeliveryIntegrationTests
{
    [Fact]
    public async Task Two_actual_letter_edit_commands_from_the_same_view_commit_one_revision_and_one_replacement_root()
    {
        var application = await SeedAsync();
        var producer = await HostAsync(false, new Gate(), new Recipient());
        var oldId = await EnqueueAsync(producer, application);
        var approvals = producer.Services.GetRequiredService<ApprovalService>();
        var expected = await approvals.LetterSnapshotAsync(application.VacancyId, Ct);
        var results = await Task.WhenAll(new[] { "first replacement", "second replacement" }
            .Select(text => approvals.SetLetterAsync(application.VacancyId, text, expected, Ct)));
        Assert.Single(results, result => result.Ok);
        Assert.Single(results, result => !result.Ok);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var saved = await context.Applications.SingleAsync(Ct);
        Assert.Equal(1, saved.Revision);
        Assert.Equal(2, await context.ApplicationDeliveryRequests.CountAsync(Ct));
        Assert.NotEqual(oldId, saved.DeliveryRequestId);
        Assert.Equal(saved.CoverLetter, (await context.Vacancies.SingleAsync(Ct)).CoverLetter);
        Assert.Equal(saved.CoverLetter, JobFinder.Pipeline.Modules.Hiring.ApplicationDeliverySources.Read(await RequestAsync(saved.DeliveryRequestId!.Value)).Text);
    }

    [Fact]
    public async Task Returning_to_identical_text_does_not_make_an_old_letter_view_current_again()
    {
        var application = await SeedAsync();
        await using (var baseline = await pg.Db.CreateDbContextAsync(Ct))
            await baseline.Vacancies.ExecuteUpdateAsync(update => update.SetProperty(row => row.CoverLetter, application.CoverLetter), Ct);
        var producer = await HostAsync(false, new Gate(), new Recipient());
        await EnqueueAsync(producer, application);
        var approvals = producer.Services.GetRequiredService<ApprovalService>();
        var old = await approvals.LetterSnapshotAsync(application.VacancyId, Ct);
        Assert.True((await approvals.SetLetterAsync(application.VacancyId, "temporary replacement", old, Ct)).Ok);
        Assert.True((await approvals.SetLetterAsync(application.VacancyId, application.CoverLetter!, await approvals.LetterSnapshotAsync(application.VacancyId, Ct), Ct)).Ok);
        Assert.False((await approvals.SetLetterAsync(application.VacancyId, "stale overwrite", old, Ct)).Ok);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(2, (await context.Applications.SingleAsync(Ct)).Revision);
        Assert.Equal(3, await context.ApplicationDeliveryRequests.CountAsync(Ct));
        Assert.Equal(application.CoverLetter, (await context.Vacancies.SingleAsync(Ct)).CoverLetter);
    }

    [Fact]
    public async Task An_unqueued_letter_view_is_revision_fenced_even_when_text_and_timestamp_return_to_the_original_values()
    {
        var application = await SeedAsync();
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            await context.Applications.ExecuteDeleteAsync(Ct);
            await context.Vacancies.ExecuteUpdateAsync(update => update.SetProperty(row => row.CoverLetter, application.CoverLetter), Ct);
        }
        var producer = await HostAsync(false, new Gate(), new Recipient());
        var approvals = producer.Services.GetRequiredService<ApprovalService>();
        var old = (await approvals.LetterSnapshotAsync(application.VacancyId, Ct))!;
        Assert.True((await approvals.SetLetterAsync(application.VacancyId, "temporary replacement", old, Ct)).Ok);
        Assert.True((await approvals.SetLetterAsync(application.VacancyId, application.CoverLetter!, await approvals.LetterSnapshotAsync(application.VacancyId, Ct), Ct)).Ok);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        await read.Vacancies.ExecuteUpdateAsync(update => update.SetProperty(row => row.UpdatedAt, old.UpdatedAt), Ct);
        var current = await approvals.LetterSnapshotAsync(application.VacancyId, Ct);
        Assert.Equal(old.Text, current!.Text);
        Assert.Equal(old.UpdatedAt, current.UpdatedAt);
        Assert.Empty(current.Applications);
        Assert.Equal(2, current.LetterRevision);
        Assert.False((await approvals.SetLetterAsync(application.VacancyId, "stale overwrite", old, Ct)).Ok);
        Assert.Equal(2, (await read.Vacancies.SingleAsync(Ct)).LetterRevision);
        Assert.Empty(await read.ApplicationDeliveryRequests.ToListAsync(Ct));
    }

    [Theory]
    [InlineData(ApplicationStatus.Sent)]
    [InlineData(ApplicationStatus.Sending)]
    [InlineData(ApplicationStatus.OutcomeUnknown)]
    public async Task A_current_but_read_only_application_cannot_be_edited_or_regenerated(ApplicationStatus status)
    {
        var application = await SeedAsync();
        var producer = await HostAsync(false, new Gate(), new Recipient());
        var approvals = producer.Services.GetRequiredService<ApprovalService>();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Applications.ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, status), Ct);
        var expected = await approvals.LetterSnapshotAsync(application.VacancyId, Ct);
        Assert.False((await approvals.SetLetterAsync(application.VacancyId, "replacement", expected, Ct)).Ok);
        Assert.False((await approvals.RegenerateLetterAsync(application.VacancyId, Ct, expected)).Result.Ok);
        Assert.Empty(await context.ApplicationDeliveryRequests.ToListAsync(Ct));
        Assert.Equal(0, (await context.Applications.SingleAsync(Ct)).Revision);
        Assert.Equal(application.Vacancy!.CoverLetter, (await context.Vacancies.SingleAsync(Ct)).CoverLetter);
    }

    [Fact]
    public async Task A_letter_command_without_the_captured_view_or_with_another_vacancy_view_is_refused()
    {
        var application = await SeedAsync();
        var other = await SeedAsync();
        var producer = await HostAsync(false, new Gate(), new Recipient());
        var approvals = producer.Services.GetRequiredService<ApprovalService>();
        Assert.False((await approvals.SetLetterAsync(application.VacancyId, "replacement", null, Ct)).Ok);
        Assert.False((await approvals.SetLetterAsync(application.VacancyId, "replacement", await approvals.LetterSnapshotAsync(other.VacancyId, Ct), Ct)).Ok);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.ApplicationDeliveryRequests.ToListAsync(Ct));
        Assert.All(await context.Applications.ToListAsync(Ct), row => Assert.Equal(0, row.Revision));
    }

    [Fact]
    public async Task A_stale_generation_button_is_refused_before_the_provider_is_called()
    {
        var application = await SeedAsync();
        var producer = await HostAsync(false, new Gate(), new Recipient());
        var approvals = producer.Services.GetRequiredService<ApprovalService>();
        var old = await approvals.LetterSnapshotAsync(application.VacancyId, Ct);
        Assert.True((await approvals.SetLetterAsync(application.VacancyId, "current replacement", old, Ct)).Ok);
        Assert.False((await approvals.RegenerateLetterAsync(application.VacancyId, Ct, old)).Result.Ok);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(1, await context.ApplicationDeliveryRequests.CountAsync(Ct));
        Assert.Equal("current replacement", (await context.Applications.SingleAsync(Ct)).CoverLetter);
    }

    [Fact]
    public async Task Letter_revision_rollback_cannot_erase_an_edited_source_witness()
    {
        var application = await SeedAsync();
        var producer = await HostAsync(false, new Gate(), new Recipient());
        var approvals = producer.Services.GetRequiredService<ApprovalService>();
        Assert.True((await approvals.SetLetterAsync(application.VacancyId, "replacement", await approvals.LetterSnapshotAsync(application.VacancyId, Ct), Ct)).Ok);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var migrations = context.Database.GetMigrations().ToArray();
        var index = Array.FindIndex(migrations, migration => migration.EndsWith("_LetterEditRevision", StringComparison.Ordinal));
        Assert.True(index > 0);
        var error = await Assert.ThrowsAsync<PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, migrations[index - 1], Ct));
        Assert.Equal("P0001", error.SqlState);
        Assert.Contains("edited source witnesses", error.MessageText);
        Assert.Contains(migrations[index], await context.Database.GetAppliedMigrationsAsync(Ct));
        Assert.Equal(1, (await context.Vacancies.SingleAsync(Ct)).LetterRevision);
        Assert.Single(await context.ApplicationDeliveryRequests.ToListAsync(Ct));
    }

    [Fact]
    public async Task A_tracked_vacancy_from_before_an_edit_cannot_overwrite_the_new_letter_revision()
    {
        var application = await SeedAsync();
        var producer = await HostAsync(false, new Gate(), new Recipient());
        var approvals = producer.Services.GetRequiredService<ApprovalService>();
        await using var stale = await pg.Db.CreateDbContextAsync(Ct);
        var vacancy = await stale.Vacancies.SingleAsync(Ct);
        Assert.True((await approvals.SetLetterAsync(application.VacancyId, "current replacement", await approvals.LetterSnapshotAsync(application.VacancyId, Ct), Ct)).Ok);
        vacancy.CoverLetter = "stale tracked replacement";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync(Ct));
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(1, (await read.Vacancies.SingleAsync(Ct)).LetterRevision);
        Assert.Equal("current replacement", (await read.Applications.SingleAsync(Ct)).CoverLetter);
        Assert.Single(await read.ApplicationDeliveryRequests.ToListAsync(Ct));
    }

    [Fact]
    public async Task The_actual_bot_edit_reply_keeps_the_prompt_version_and_cannot_overwrite_a_later_edit()
    {
        var application = await SeedAsync();
        var producer = await HostAsync(false, new Gate(), new Recipient());
        var approvals = producer.Services.GetRequiredService<ApprovalService>();
        var constructor = typeof(BotService).GetConstructors().Single();
        var arguments = constructor.GetParameters().Select(parameter => parameter.ParameterType == typeof(BotOptions)
            ? (object)new BotOptions { OwnerUserId = 123456 }
            : parameter.ParameterType == typeof(OwnerBotMessages)
                ? new OwnerBotMessages(new BotOptions { OwnerUserId = 123456 }, producer.Services.GetRequiredService<JobFinder.Pipeline.Notifications.NotificationDeliveryRequests>(), TimeProvider.System)
            : producer.Services.GetService(parameter.ParameterType)).ToArray();
        using var service = (BotService)constructor.Invoke(arguments);
        using var handler = new SyntheticBotApi();
        using var http = new HttpClient(handler);
        var bot = new TelegramBotClient(new TelegramBotClientOptions("123456:synthetic-test-token"), http);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var callback = new CallbackQuery { Id = "synthetic-callback", From = new User { Id = 123456, FirstName = "Synthetic" } };
        await InvokeBotAsync(service, "AskForTextAsync", bot, callback, "letter", application.VacancyId, "Synthetic edit prompt", Ct, null!);
        var edit = Assert.IsType<JobFinder.Pipeline.Career.CapturedBotEdit>(await producer.Services.GetRequiredService<JobFinder.Pipeline.Career.BotEdits>()
            .FindAsync(123456, 123456, 50, (await context.Notifications.AsNoTracking().SingleAsync(row => row.Kind == NotificationKind.EditPrompt, Ct)).Text, 123456, Ct));
        Assert.True((await approvals.SetLetterAsync(application.VacancyId, "later edit", await approvals.LetterSnapshotAsync(application.VacancyId, Ct), Ct)).Ok);
        await InvokeBotAsync(service, "OnEditReplyAsync", bot, new Message { Id = 900, Text = "stale bot edit", Chat = new Chat { Id = 123456 } }, edit, Ct);
        Assert.Equal("later edit", (await context.Applications.SingleAsync(Ct)).CoverLetter);
        Assert.Equal(1, await context.ApplicationDeliveryRequests.CountAsync(Ct));
        Assert.Contains(await context.Notifications.AsNoTracking().ToListAsync(Ct), row => row.Text.Contains("изменились", StringComparison.Ordinal));
        Assert.Empty(handler.Texts);
    }

    private static Task InvokeBotAsync(BotService service, string method, params object[] arguments) =>
        (Task)typeof(BotService).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, arguments)!;

    private sealed class SyntheticBotApi : HttpMessageHandler
    {
        public List<string> Texts { get; } = [];
        public List<string> CallbackAnswers { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("api.telegram.org", request.RequestUri!.Host);
            var send = request.RequestUri.AbsolutePath.EndsWith("/sendMessage", StringComparison.OrdinalIgnoreCase);
            if (send)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Texts.Add(body.RootElement.GetProperty("text").GetString()!);
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/answerCallbackQuery", StringComparison.OrdinalIgnoreCase))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                if (body.RootElement.TryGetProperty("text", out var text)) CallbackAnswers.Add(text.GetString()!);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(send ? """{"ok":true,"result":{"message_id":50,"date":1790800000,"chat":{"id":123456,"type":"private"}}}""" : """{"ok":true,"result":true}""", Encoding.UTF8, "application/json"),
            };
        }
    }
}
