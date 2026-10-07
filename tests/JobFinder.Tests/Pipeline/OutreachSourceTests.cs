using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Tests.Pipeline;

public sealed partial class OrdinaryNotificationAdmissionTests
{
    private async Task<(Vacancy Vacancy, CompanyContact Contact)> OutreachSourceAsync(bool profile = true, ContactKind kind = ContactKind.LinkedIn, bool consumer = false)
    {
        var vacancy = await VacancyAsync();
        var host = _host ?? await HostAsync(consumer);
        if (profile)
        {
            var profiles = host.Services.GetRequiredService<ProfileProvider>();
            await profiles.SaveAsync(new CandidateProfile { Name = "Captured synthetic candidate", Summary = OutreachProvider.Proof, Reviewed = true },
                (await profiles.GetSnapshotAsync(Ct))?.Version ?? 0, "synthetic-outreach", Ct);
        }
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var contact = new CompanyContact
        {
            CompanyKey = VacancyFingerprint.NormalizeCompany(vacancy.CompanyName), CompanyName = vacancy.CompanyName,
            Kind = kind, Value = "https://linkedin.invalid/in/" + Guid.NewGuid().ToString("N"), Source = "manual",
            PersonName = "Captured synthetic recruiter", Role = "Recruiter", FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
        };
        context.CompanyContacts.Add(contact);
        await context.SaveChangesAsync(Ct);
        contact = await context.CompanyContacts.AsNoTracking().SingleAsync(row => row.Id == contact.Id, Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        return (vacancy, contact);
    }

    private static Task<Outreach?> PublishNoteAsync(OutreachSources owner, OutreachDraftSnapshot snapshot, string text) =>
        owner.PublishAsync(snapshot, text, null, null, Ct, new LetterDraft(text, [], [], new ApplicationLetterReview
        {
            TextHash = Fingerprint.Of(text), ContextHash = "synthetic-prepaid-note", Score = 10, CheckedAt = DateTimeOffset.UtcNow,
        }));

    [Theory]
    [InlineData("missing")]
    [InlineData("score")]
    [InlineData("problems")]
    [InlineData("text")]
    [InlineData("hash")]
    [InlineData("context")]
    [InlineData("length")]
    public async Task Automatic_LinkedIn_publication_refuses_unchecked_or_altered_notes_without_source_or_card(string damage)
    {
        var (vacancy, contact) = await OutreachSourceAsync();
        var owner = _host!.Services.GetRequiredService<OutreachSources>();
        var captured = Assert.IsType<CapturedOutreachDraft>(await owner.CaptureAsync(vacancy.Id, contact.Id, automatic: true, Ct));
        var text = damage == "length" ? new string('x', LetterLayout.LinkedInLimit + 1) : "Synthetic checked note";
        var review = new ApplicationLetterReview
        {
            Score = damage == "score" ? 8 : 10, TextHash = damage == "hash" ? "wrong" : Fingerprint.Of(text),
            ContextHash = damage == "context" ? "" : "synthetic-review-context", CheckedAt = DateTimeOffset.UtcNow,
        };
        var draft = damage == "missing" ? null : new LetterDraft(damage == "text" ? "Other note" : text, [],
            damage == "problems" ? ["Unresolved quality problem"] : [], review);
        Assert.Null(await owner.PublishAsync(captured.Snapshot, text, null, null, Ct, draft));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.Outreaches.ToListAsync(Ct));
        await NoAdmissionAsync(context);
        Assert.Equal(0, _provider.Calls);
    }

    [Fact]
    public async Task Captured_outreach_facts_are_detached_and_remain_the_input_before_candidate_change()
    {
        var (vacancy, contact) = await OutreachSourceAsync();
        var facts = _host!.Services.GetRequiredService<FactsProvider>();
        await facts.GetAsync(Ct);
        await facts.UpsertAsync("location", "Город?", "Минск", Ct);
        var owner = _host.Services.GetRequiredService<OutreachSources>();
        var captured = Assert.IsType<CapturedOutreachDraft>(await owner.CaptureAsync(vacancy.Id, contact.Id, automatic: true, Ct));
        await facts.UpsertAsync("location", "Город?", "Другой город", Ct);
        Assert.Contains("Минск", captured.Facts.ForPrompt());
        Assert.DoesNotContain("Другой город", captured.Facts.ForPrompt());
        Assert.Null(await PublishNoteAsync(owner, captured.Snapshot, "Synthetic checked note"));
    }

    [Fact]
    public async Task Explicit_LinkedIn_contact_drafting_uses_the_same_native_reviewed_note_and_warm_request()
    {
        var (vacancy, contact) = await OutreachSourceAsync(consumer: true);
        var service = _host!.Services.GetRequiredService<OutreachService>();
        Assert.True((await service.DraftForContactAsync(vacancy.Id, contact.Id, null, Ct)).Ok);
        var paidCalls = _provider.Calls;
        Assert.InRange(paidCalls, 2, 4);
        Assert.True((await service.DraftForContactAsync(vacancy.Id, contact.Id, null, Ct)).Ok);
        Assert.Equal(paidCalls, _provider.Calls);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var notes = await context.Outreaches.ToListAsync(Ct);
        Assert.Equal(2, notes.Count);
        Assert.All(notes, note =>
        {
            Assert.InRange(note.Text.Length, 1, LetterLayout.LinkedInLimit);
            Assert.Contains(vacancy.Url, note.Text);
            Assert.Equal(notes[0].Text, note.Text);
        });
        Assert.Single(await context.LetterPreparationRequests.Where(row => row.VacancyId == vacancy.Id).ToListAsync(Ct));
        Assert.Empty(await context.EmployerMessages.ToListAsync(Ct));
        Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Outreach_source_native_admission_failure_can_retry_the_same_prepaid_snapshot(bool fail)
    {
        var (vacancy, contact) = await OutreachSourceAsync();
        var owner = _host!.Services.GetRequiredService<OutreachSources>();
        var captured = Assert.IsType<CapturedOutreachDraft>(await owner.CaptureAsync(vacancy.Id, contact.Id, automatic: true, Ct));
        _fault.Active = fail;
        if (fail)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => PublishNoteAsync(owner, captured.Snapshot, "Synthetic note"));
            await using var failed = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Empty(await failed.Outreaches.ToListAsync(Ct));
            await NoAdmissionAsync(failed);
            _fault.Active = false;
        }
        var source = Assert.IsType<Outreach>(await PublishNoteAsync(owner, captured.Snapshot, "Synthetic note"));
        Assert.Null(await PublishNoteAsync(owner, captured.Snapshot, "Different replay text"));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(source.Id, (await context.Outreaches.SingleAsync(Ct)).Id);
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
        Assert.Equal(0, _provider.Calls);
    }

    [Fact]
    public async Task Automatic_company_week_cap_is_atomic_across_different_vacancies()
    {
        var (first, contact) = await OutreachSourceAsync();
        var second = await VacancyAsync();
        var owner = _host!.Services.GetRequiredService<OutreachSources>();
        var one = Assert.IsType<CapturedOutreachDraft>(await owner.CaptureAsync(first.Id, contact.Id, automatic: true, Ct));
        var two = Assert.IsType<CapturedOutreachDraft>(await owner.CaptureAsync(second.Id, contact.Id, automatic: true, Ct));
        var results = await Task.WhenAll(PublishNoteAsync(owner, one.Snapshot, "First note"),
            PublishNoteAsync(owner, two.Snapshot, "Second note"));
        Assert.Single(results, result => result is not null);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.Outreaches.ToListAsync(Ct));
        Assert.Single(await context.Notifications.ToListAsync(Ct));
        Assert.Null(await owner.CaptureAsync(first.Id, contact.Id, automatic: true, Ct));
        Assert.Null(await owner.CaptureAsync(second.Id, contact.Id, automatic: true, Ct));
    }

    [Fact]
    public async Task Explicit_manual_draft_bypasses_week_cap_but_cannot_replay_a_captured_publication()
    {
        var (vacancy, contact) = await OutreachSourceAsync();
        var owner = _host!.Services.GetRequiredService<OutreachSources>();
        var automatic = Assert.IsType<CapturedOutreachDraft>(await owner.CaptureAsync(vacancy.Id, contact.Id, automatic: true, Ct));
        Assert.NotNull(await PublishNoteAsync(owner, automatic.Snapshot, "Automatic note"));
        var manual = Assert.IsType<CapturedOutreachDraft>(await owner.CaptureAsync(vacancy.Id, contact.Id, automatic: false, Ct));
        Assert.NotNull(await PublishNoteAsync(owner, manual.Snapshot, "Manual note"));
        Assert.Null(await PublishNoteAsync(owner, manual.Snapshot, "Replay"));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(2, await context.Outreaches.CountAsync(Ct));
        Assert.Equal(2, await context.Notifications.CountAsync(Ct));
    }

    [Theory]
    [InlineData("contact-value")]
    [InlineData("contact-name")]
    [InlineData("contact-role")]
    [InlineData("contact-kind")]
    [InlineData("contact-source")]
    [InlineData("contact-company")]
    [InlineData("contact-deleted")]
    [InlineData("vacancy-text")]
    [InlineData("salary")]
    [InlineData("skills")]
    [InlineData("experience")]
    [InlineData("city")]
    [InlineData("vacancy-dismissed")]
    [InlineData("vacancy-deleted")]
    [InlineData("conversation")]
    [InlineData("profile")]
    [InlineData("facts")]
    [InlineData("story")]
    [InlineData("prior-outreach")]
    public async Task Outreach_publication_refuses_changed_prepaid_source_and_candidate_witnesses(string changed)
    {
        var (vacancy, contact) = await OutreachSourceAsync();
        var owner = _host!.Services.GetRequiredService<OutreachSources>();
        var captured = Assert.IsType<CapturedOutreachDraft>(await owner.CaptureAsync(vacancy.Id, contact.Id, automatic: true, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var currentContact = await context.CompanyContacts.SingleAsync(row => row.Id == contact.Id, Ct);
        var currentVacancy = await context.Vacancies.SingleAsync(row => row.Id == vacancy.Id, Ct);
        switch (changed)
        {
            case "contact-value": currentContact.Value += "-changed"; break;
            case "contact-name": currentContact.PersonName = "Changed recruiter"; break;
            case "contact-role": currentContact.Role = "Changed role"; break;
            case "contact-kind": currentContact.Kind = ContactKind.Phone; break;
            case "contact-source": currentContact.Source = "Changed source"; break;
            case "contact-company": currentContact.CompanyKey = "another company"; break;
            case "contact-deleted": context.CompanyContacts.Remove(currentContact); break;
            case "vacancy-text": currentVacancy.Description += " Changed"; break;
            case "salary": currentVacancy.SalaryFrom = 4500; break;
            case "skills": currentVacancy.Skills = ["Changed skill"]; break;
            case "experience": currentVacancy.Experience = "Changed experience"; break;
            case "city": currentVacancy.City = "Changed city"; break;
            case "vacancy-dismissed": currentVacancy.Status = VacancyStatus.Dismissed; break;
            case "vacancy-deleted": context.Vacancies.Remove(currentVacancy); break;
            case "conversation":
                context.Applications.Add(new Application
                {
                    VacancyId = vacancy.Id, Channel = ApplicationChannel.Email, Status = ApplicationStatus.Sent,
                    CreatedAt = DateTimeOffset.UtcNow, Revision = 1,
                    Events = [new ApplicationEvent { Kind = EventKind.MessageIn, Channel = EventChannel.Email, Text = "New question", At = DateTimeOffset.UtcNow }],
                });
                break;
            case "profile":
                var profiles = _host!.Services.GetRequiredService<ProfileProvider>();
                await profiles.SaveAsync(new CandidateProfile { Name = "Changed synthetic profile", Reviewed = true }, captured.Snapshot.ProfileVersion, "test", Ct);
                break;
            case "facts": await _host!.Services.GetRequiredService<FactsProvider>().UpsertAsync("synthetic", "Question", "Changed fact", Ct); break;
            case "story": context.Stories.Add(new Story { Title = "Changed synthetic story", Confirmed = true, Action = Guid.NewGuid().ToString("N") }); break;
            case "prior-outreach": context.Outreaches.Add(new Outreach { VacancyId = vacancy.Id, ContactId = contact.Id, Text = "Concurrent outreach", Status = OutreachStatus.Draft, CreatedAt = DateTimeOffset.UtcNow }); break;
        }
        await context.SaveChangesAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        Assert.Null(await PublishNoteAsync(owner, captured.Snapshot, "Stale paid note"));
        Assert.Equal(changed == "prior-outreach" ? 1 : 0, await context.Outreaches.CountAsync(Ct));
        await NoAdmissionAsync(context);
        Assert.Equal(0, _provider.Calls);
    }

    [Theory]
    [InlineData("contact")]
    [InlineData("profile")]
    [InlineData("conversation")]
    [InlineData("duplicate")]
    public async Task Actual_paid_outreach_preparation_does_not_publish_against_changed_inputs(string changed)
    {
        var (vacancy, contact) = await OutreachSourceAsync(consumer: true);
        await _host!.Services.GetRequiredService<FactsProvider>().GetAsync(Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        _provider.Response = "Synthetic note";
        _provider.BeforeResponse = async () =>
        {
            if (changed == "contact")
                await context.CompanyContacts.Where(row => row.Id == contact.Id).ExecuteUpdateAsync(update => update.SetProperty(row => row.Value, "https://linkedin.invalid/in/changed"), Ct);
            if (changed == "profile")
            {
                var profiles = _host!.Services.GetRequiredService<ProfileProvider>();
                await profiles.SaveAsync(new CandidateProfile { Name = "New synthetic candidate", Reviewed = true },
                    (await profiles.GetSnapshotAsync(Ct))!.Version, "test", Ct);
            }
            if (changed == "conversation")
            {
                context.Applications.Add(new Application { VacancyId = vacancy.Id, Status = ApplicationStatus.Sent, Channel = ApplicationChannel.Email, CreatedAt = DateTimeOffset.UtcNow, Revision = 1 });
                await context.SaveChangesAsync(Ct);
            }
            if (changed == "duplicate")
            {
                context.Outreaches.Add(new Outreach { VacancyId = vacancy.Id, ContactId = contact.Id, Text = "Concurrent note", Status = OutreachStatus.Draft, CreatedAt = DateTimeOffset.UtcNow });
                await context.SaveChangesAsync(Ct);
            }
            await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        };
        Assert.Null(await _host!.Services.GetRequiredService<OutreachService>().PrepareAsync(vacancy.Id, Ct));
        Assert.Equal(changed == "duplicate" ? 1 : 0, await context.Outreaches.CountAsync(Ct));
        if (changed is "profile" or "conversation")
        {
            var notice = await context.Notifications.SingleAsync(Ct);
            Assert.Equal(NotificationKind.Alert, notice.Kind);
            Assert.Contains("letter source or candidate superseded", notice.Text);
            await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
        }
        else await NoAdmissionAsync(context);
        Assert.Contains(_provider.Requests, request => request.Contains("Captured synthetic candidate", StringComparison.Ordinal));
        Assert.Contains(_provider.Requests, request => request.Contains("Captured synthetic recruiter", StringComparison.Ordinal));
        Assert.DoesNotContain(_provider.Requests, request => request.Contains("New synthetic candidate", StringComparison.Ordinal));
        Assert.InRange(_provider.Calls, 1, 4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Draft_warnings_are_preserved_for_manual_outreach_but_automatic_publication_refuses_them(bool automatic)
    {
        var (vacancy, contact) = await OutreachSourceAsync();
        var owner = _host!.Services.GetRequiredService<OutreachSources>();
        var captured = Assert.IsType<CapturedOutreachDraft>(await owner.CaptureAsync(vacancy.Id, contact.Id, automatic, Ct));
        var draft = new RecruiterDraft(new string('x', 280), false, RecruiterSituation.NotApplied, false, [], ["Synthetic warning"]);
        var published = await owner.PublishAsync(captured.Snapshot, draft.Text, draft.Fresh, draft, Ct);
        Assert.Equal(!automatic, published is not null);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (automatic) await NoAdmissionAsync(context);
        else
        {
            Assert.Equal(draft.Text, published!.Text);
            var notice = await context.Notifications.SingleAsync(Ct);
            Assert.Contains("Synthetic warning", notice.Text);
            await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
        }
    }
}
