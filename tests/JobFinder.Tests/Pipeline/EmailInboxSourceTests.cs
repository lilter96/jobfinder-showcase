using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Integrations.Email;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Tests.Pipeline;

public sealed partial class HhNegotiationSourceIntegrationTests
{
    private static string MailAccount(string username = "synthetic@invalid") => InboxIdentity.Account(new() { Username = username });

    private async Task<(Application Application, EmailInboxSources Owner, EmailInboxSnapshot Snapshot)> InboxAsync(SaveChangesInterceptor? fault = null)
    {
        var application = await SeedAsync();
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            await context.Applications.Where(row => row.Id == application.Id).ExecuteUpdateAsync(update => update.SetProperty(row => row.EmailMessageId, "synthetic-outbound@invalid"), Ct);
        await SourcesAsync(fault);
        var owner = _host!.Services.GetRequiredService<EmailInboxSources>();
        return (application, owner, await owner.CaptureAsync(MailAccount(), Ct));
    }

    private static InboxMessage Mail(uint uid, string? id = null, string author = "recruiter@synthetic.invalid", bool thread = true) =>
        new(uid, id ?? $"synthetic-{uid}@invalid", null, thread ? ["synthetic-outbound@invalid"] : [], author,
            "Synthetic Recruiter", "Synthetic subject", "Synthetic complete mail", DateTimeOffset.UtcNow);

    private static InboxBatch MailBatch(EmailInboxSnapshot snapshot, uint validity = 1, params InboxMessage[] messages)
    {
        var position = EmailInboxSources.Position(snapshot);
        return new() { AccountId = snapshot.AccountId, UidValidity = validity,
            AfterUid = position.AfterUidFor(snapshot.AccountId, validity), ReplayAll = position.ReplayAllFor(snapshot.AccountId, validity), Messages = [.. messages] };
    }

    private async Task<EmailInboxState> MailStateAsync(string accountId)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return JsonSerializer.Deserialize((await context.Settings.SingleAsync(row => row.Key == EmailInboxSources.Prefix + accountId, Ct)).Value,
            EmailInboxJsonContext.Default.EmailInboxState)!;
    }

    [Fact]
    public async Task Inbox_batch_cursor_receipt_and_incoming_command_commit_once_without_paid_work()
    {
        var (application, owner, snapshot) = await InboxAsync();
        var batch = MailBatch(snapshot, 17, Mail(12, author: "self@invalid"), Mail(10), Mail(11, author: "unmatched@invalid", thread: false), Mail(13, "synthetic-10@invalid"));
        var result = await owner.PublishAsync(snapshot, batch, "self@invalid", false, Ct);
        Assert.True(result.Published);
        Assert.Equal(4, result.Items);
        Assert.Equal(2, result.Matched);
        Assert.False(result.HhRefreshNeeded);
        Assert.Equal(0, (await owner.PublishAsync(snapshot, batch, "self@invalid", false, Ct)).Items);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var root = await context.ReplyProcessingRequests.SingleAsync(Ct);
        Assert.Equal(application.Id, root.ApplicationId);
        Assert.Equal(EventChannel.Email, ReplyProcessingRequests.ReadRaw(root).Channel);
        Assert.Equal("synthetic-10@invalid", ReplyProcessingRequests.ReadRaw(root).ExternalId);
        Assert.Equal(1, await context.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM jobfinder_queues.wolverine_queue_hiring_replies").SingleAsync(Ct));
        var state = await MailStateAsync(snapshot.AccountId);
        Assert.Equal(17u, state.Position.UidValidity);
        Assert.Equal(13u, state.Position.LastUid);
        var receipt = JsonSerializer.Deserialize((await context.Settings.SingleAsync(row => row.Key.StartsWith(EmailInboxSources.Prefix + "history."), Ct)).Value,
            EmailInboxJsonContext.Default.EmailInboxReceipt)!;
        Assert.Null(receipt.BeforeJson);
        Assert.Equal([10u, 11u, 12u, 13u], receipt.Observations.Select(row => row.Uid));
        Assert.Equal(["incoming", "unmatched", "self", "incoming"], receipt.Observations.Select(row => row.Disposition));
        Assert.Empty(await context.ApplicationEvents.ToListAsync(Ct));
        Assert.Empty(await context.LlmCalls.ToListAsync(Ct));
        Assert.Empty(await context.EmployerMessages.ToListAsync(Ct));
    }

    [Fact]
    public async Task Inbox_failure_rolls_back_cursor_preimage_root_and_native_command_and_recreation_reuses_the_capture()
    {
        var fault = new InboxFault();
        var (_, owner, snapshot) = await InboxAsync(fault);
        var batch = MailBatch(snapshot, 1, Mail(10));
        fault.Active = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.PublishAsync(snapshot, batch, "self@invalid", false, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.Settings.Where(row => row.Key.StartsWith(EmailInboxSources.Prefix)).ToListAsync(Ct));
        Assert.Empty(await context.ReplyProcessingRequests.ToListAsync(Ct));
        Assert.Equal(0, await context.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM jobfinder_queues.wolverine_queue_hiring_replies").SingleAsync(Ct));
        fault.Active = false;
        owner = ActivatorUtilities.CreateInstance<EmailInboxSources>(_host!.Services);
        Assert.True((await owner.PublishAsync(snapshot, batch, "self@invalid", false, Ct)).Published);
        Assert.Single(await context.ReplyProcessingRequests.ToListAsync(Ct));
    }

    [Fact]
    public async Task Inbox_concurrent_admission_has_one_receipt_and_stale_different_batch_cannot_advance_the_cursor()
    {
        var (_, owner, snapshot) = await InboxAsync();
        var batch = MailBatch(snapshot, 1, Mail(10));
        var results = await Task.WhenAll(owner.PublishAsync(snapshot, batch, "self@invalid", false, Ct),
            owner.PublishAsync(snapshot, batch, "self@invalid", false, Ct));
        Assert.Equal(1, results.Sum(row => row.Items));
        Assert.All(results, row => Assert.True(row.Published));
        Assert.False((await owner.PublishAsync(snapshot, MailBatch(snapshot, 1, Mail(20)), "self@invalid", false, Ct)).Published);
        Assert.Equal(10u, (await MailStateAsync(snapshot.AccountId)).Position.LastUid);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.ReplyProcessingRequests.ToListAsync(Ct));
        Assert.Single(await context.Settings.Where(row => row.Key.StartsWith(EmailInboxSources.Prefix + "history.")).ToListAsync(Ct));
    }

    [Fact]
    public async Task Inbox_account_and_uidvalidity_changes_do_not_reuse_old_uids_and_resync_requires_manual_reply()
    {
        var (_, owner, snapshot) = await InboxAsync();
        Assert.True((await owner.PublishAsync(snapshot, MailBatch(snapshot, 1, Mail(640)), "self@invalid", false, Ct)).Published);
        var reset = await owner.CaptureAsync(snapshot.AccountId, Ct);
        var batch = MailBatch(reset, 2, Mail(1, InboxIdentity.MessageId(snapshot.AccountId, 2, 1)));
        batch.HasMore = true;
        Assert.True(batch.ReplayAll);
        Assert.Equal(0u, batch.AfterUid);
        Assert.True((await owner.PublishAsync(reset, batch, "self@invalid", false, Ct)).Published);
        var tail = await owner.CaptureAsync(snapshot.AccountId, Ct);
        var completed = MailBatch(tail, 2);
        Assert.True((await owner.PublishAsync(tail, completed, "self@invalid", false, Ct)).Published);
        Assert.False((await MailStateAsync(snapshot.AccountId)).Position.ReplayAll);
        var next = await owner.CaptureAsync(snapshot.AccountId, Ct);
        Assert.True((await owner.PublishAsync(next, MailBatch(next, 2, Mail(2)), "self@invalid", false, Ct)).Published);
        var changedAccount = await owner.CaptureAsync(MailAccount("other@invalid"), Ct);
        Assert.Equal(0u, MailBatch(changedAccount, 1).AfterUid);
        Assert.True((await owner.PublishAsync(changedAccount, MailBatch(changedAccount, 1,
            Mail(1, InboxIdentity.MessageId(changedAccount.AccountId, 1, 1))), "self@invalid", false, Ct)).Published);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var roots = await context.ReplyProcessingRequests.OrderBy(row => row.CreatedAt).ToListAsync(Ct);
        Assert.Equal(4, roots.Count);
        Assert.True(ReplyProcessingRequests.ReadRaw(roots.Single(row => ReplyProcessingRequests.ReadRaw(row).ExternalId == batch.Messages[0].MessageId)).YourChat);
        Assert.False(ReplyProcessingRequests.ReadRaw(roots.Single(row => ReplyProcessingRequests.ReadRaw(row).ExternalId == "synthetic-2@invalid")).YourChat);
        Assert.Equal(2u, (await MailStateAsync(snapshot.AccountId)).Position.LastUid);
        Assert.Equal(1u, (await MailStateAsync(changedAccount.AccountId)).Position.LastUid);
    }

    [Theory]
    [InlineData("{\"version\":99}")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("broken")]
    public async Task Inbox_damaged_cursor_fails_closed_without_incoming_work(string json)
    {
        var (_, owner, snapshot) = await InboxAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.Settings.Add(new() { Key = EmailInboxSources.Prefix + snapshot.AccountId, Value = json });
        await context.SaveChangesAsync(Ct);
        await Assert.ThrowsAnyAsync<Exception>(() => owner.CaptureAsync(snapshot.AccountId, Ct));
        Assert.Empty(await context.ReplyProcessingRequests.ToListAsync(Ct));
    }

    [Fact]
    public async Task Inbox_cursor_with_missing_or_mismatched_receipt_cannot_skip_messages()
    {
        var (_, owner, snapshot) = await InboxAsync();
        Assert.True((await owner.PublishAsync(snapshot, MailBatch(snapshot, 1, Mail(10)), "self@invalid", false, Ct)).Published);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var row = await context.Settings.SingleAsync(setting => setting.Key == EmailInboxSources.Prefix + snapshot.AccountId, Ct);
        var original = row.Value;
        var state = JsonSerializer.Deserialize(original, EmailInboxJsonContext.Default.EmailInboxState)!;
        state.Position.LastUid = uint.MaxValue;
        row.Value = JsonSerializer.Serialize(state, EmailInboxJsonContext.Default.EmailInboxState);
        await context.SaveChangesAsync(Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.CaptureAsync(snapshot.AccountId, Ct));
        row.Value = original;
        await context.SaveChangesAsync(Ct);
        await context.Settings.Where(setting => setting.Key.StartsWith(EmailInboxSources.Prefix + "history.")).ExecuteDeleteAsync(Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.CaptureAsync(snapshot.AccountId, Ct));
        Assert.Single(await context.ReplyProcessingRequests.ToListAsync(Ct));
    }

    [Fact]
    public async Task Inbox_duplicate_rfc_message_ids_in_a_new_uidvalidity_do_not_create_another_paid_operation()
    {
        var (_, owner, snapshot) = await InboxAsync();
        var first = Mail(100, "preserved-rfc@invalid");
        Assert.True((await owner.PublishAsync(snapshot, MailBatch(snapshot, 1, first), "self@invalid", false, Ct)).Published);
        var reset = await owner.CaptureAsync(snapshot.AccountId, Ct);
        Assert.True((await owner.PublishAsync(reset, MailBatch(reset, 2, first with { Uid = 1 }), "self@invalid", false, Ct)).Published);
        Assert.Equal(1u, (await MailStateAsync(snapshot.AccountId)).Position.LastUid);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.ReplyProcessingRequests.ToListAsync(Ct));
        Assert.Empty(await context.LlmCalls.ToListAsync(Ct));
    }

    [Fact]
    public async Task Inbox_legacy_uid_is_preserved_but_never_trusted_without_mailbox_identity_and_generic_writes_refuse()
    {
        var (_, owner, snapshot) = await InboxAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.Settings.Add(new() { Key = EmailInboxSources.LegacyKey, Value = "4294967295" });
        await context.SaveChangesAsync(Ct);
        snapshot = await owner.CaptureAsync(snapshot.AccountId, Ct);
        Assert.Equal(0u, MailBatch(snapshot, 1).AfterUid);
        Assert.True((await owner.PublishAsync(snapshot, MailBatch(snapshot, 1, Mail(1)), "self@invalid", false, Ct)).Published);
        Assert.Equal("4294967295", (await MailStateAsync(snapshot.AccountId)).LegacyJson);
        Assert.Equal("4294967295", (await context.Settings.SingleAsync(row => row.Key == EmailInboxSources.LegacyKey, Ct)).Value);
        var settings = _host!.Services.GetRequiredService<RuntimeSettings>();
        foreach (var key in new[] { EmailInboxSources.LegacyKey, EmailInboxSources.Prefix + snapshot.AccountId, EmailInboxSources.HhRefreshKey })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => settings.SetAsync(key, "0", Ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => settings.RemoveAsync(key, Ct));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Inbox_hh_hint_survives_recreation_and_is_acknowledged_only_with_successful_hh_source_commit(bool session)
    {
        var fault = new InboxFault();
        var (application, owner, snapshot) = await InboxAsync(fault);
        var message = Mail(10, author: "notifications@hh.ru", thread: false) with { Text = application.Vacancy!.Title };
        var result = await owner.PublishAsync(snapshot, MailBatch(snapshot, 1, message), "self@invalid", session, Ct);
        Assert.True(result.HhRefreshNeeded);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(session ? 0 : 1, await context.ReplyProcessingRequests.CountAsync(Ct));
        owner = ActivatorUtilities.CreateInstance<EmailInboxSources>(_host!.Services);
        var next = await owner.CaptureAsync(snapshot.AccountId, Ct);
        Assert.True((await owner.PublishAsync(next, MailBatch(next), "self@invalid", session, Ct)).HhRefreshNeeded);
        var hh = _host.Services.GetRequiredService<HhNegotiationSources>();
        await hh.PublishAsync([], snapshot.CapturedAt.AddSeconds(-1), Ct);
        Assert.True((await owner.PublishAsync(next, MailBatch(next), "self@invalid", session, Ct)).HhRefreshNeeded);
        fault.Active = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => hh.PublishAsync([], snapshot.CapturedAt.AddSeconds(1), Ct));
        fault.Active = false;
        Assert.True((await owner.PublishAsync(next, MailBatch(next), "self@invalid", session, Ct)).HhRefreshNeeded);
        await hh.PublishAsync([], snapshot.CapturedAt.AddSeconds(1), Ct);
        Assert.False((await owner.PublishAsync(next, MailBatch(next), "self@invalid", session, Ct)).HhRefreshNeeded);
    }

    private sealed class InboxFault : SaveChangesInterceptor
    {
        public bool Active { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Active && eventData.Context is JobFinder.Data.JobFinderDbContext context
                && context.ChangeTracker.Entries<AppSetting>().Any(entry => EmailInboxSources.OwnsKey(entry.Entity.Key)
                    && entry.State is EntityState.Added or EntityState.Modified)) throw new InvalidOperationException("Synthetic mailbox admission rollback");
            return ValueTask.FromResult(result);
        }
    }
}

public sealed class InboxPolicyTests
{
    [Fact]
    public void Actual_mime_parser_preserves_rfc_identity_thread_headers_and_text_or_binds_a_missing_id_to_the_mailbox_epoch()
    {
        var sender = new EmailSender(new() { Username = "synthetic@invalid" });
        using var message = new MimeKit.MimeMessage
        {
            MessageId = "original@synthetic.invalid", InReplyTo = "original-outbound@synthetic.invalid",
            Subject = "Synthetic subject", Date = DateTimeOffset.UtcNow, Body = new MimeKit.TextPart("plain") { Text = "Synthetic plain text" },
        };
        message.References.Add("original-outbound@synthetic.invalid");
        message.From.Add(new MimeKit.MailboxAddress("Synthetic Recruiter", "recruiter@synthetic.invalid"));
        var read = sender.ToInbox(new MailKit.UniqueId(10), message, 17);
        Assert.Equal(message.MessageId, read.MessageId);
        Assert.Equal(message.InReplyTo, read.InReplyTo);
        Assert.Equal(message.References, read.References);
        Assert.Equal("recruiter@synthetic.invalid", read.FromAddress);
        Assert.Equal("Synthetic plain text", read.Text);
        message.Headers.RemoveAll(MimeKit.HeaderId.MessageId);
        var missing = sender.ToInbox(new MailKit.UniqueId(10), message, 17);
        Assert.Equal(InboxIdentity.MessageId(sender.InboxAccountId, 17, 10), missing.MessageId);
        Assert.NotEqual(missing.MessageId, sender.ToInbox(new MailKit.UniqueId(10), message, 18).MessageId);
        Assert.Throws<ArgumentException>(() => sender.ToInbox(new MailKit.UniqueId(10), message, 0));
    }

    [Theory]
    [InlineData(17u, 17u, true)]
    [InlineData(17u, 18u, false)]
    [InlineData(0u, 0u, false)]
    public void Mailbox_epoch_change_during_read_refuses_admission(uint expected, uint observed, bool accepted)
    {
        if (accepted) EmailSender.CheckValidity(expected, observed);
        else Assert.Throws<InvalidOperationException>(() => EmailSender.CheckValidity(expected, observed));
    }

    [Fact]
    public void Uids_are_scoped_to_account_and_validity_including_uint_max_without_wrapping()
    {
        var position = new InboxPosition { AccountId = "account", UidValidity = 10, LastUid = uint.MaxValue };
        Assert.Equal(uint.MaxValue, position.AfterUidFor("account", 10));
        Assert.Equal(0u, position.AfterUidFor("account", 11));
        Assert.True(position.ReplayAllFor("account", 11));
        Assert.Equal(0u, position.AfterUidFor("other", 10));
        Assert.False(position.ReplayAllFor("other", 10));
        Assert.Throws<ArgumentException>(() => position.AfterUidFor("account", 0));
        var fresh = new InboxPosition { AccountId = "account" };
        Assert.False(fresh.ReplayAllFor("account", 10));
    }

    [Fact]
    public void Password_rotation_keeps_account_identity_but_host_port_or_login_changes_do_not()
    {
        var options = new EmailOptions { Username = "synthetic@invalid", Password = "synthetic-old" };
        var identity = InboxIdentity.Account(options);
        options.Password = "synthetic-new";
        Assert.Equal(identity, InboxIdentity.Account(options));
        options.ImapPort++;
        Assert.NotEqual(identity, InboxIdentity.Account(options));
        options.ImapPort--;
        options.Username = "other@invalid";
        Assert.NotEqual(identity, InboxIdentity.Account(options));
    }

    [Fact]
    public void Missing_message_id_fallback_is_valid_only_for_its_original_account_validity_and_uid()
    {
        var identity = InboxIdentity.MessageId("account", 10, uint.MaxValue);
        Assert.Equal(uint.MaxValue, InboxIdentity.UidFor(identity, "account", 10));
        Assert.Null(InboxIdentity.UidFor(identity, "other", 10));
        Assert.Null(InboxIdentity.UidFor(identity, "account", 11));
        Assert.Null(InboxIdentity.UidFor("imap:account:10:0", "account", 10));
        Assert.Null(InboxIdentity.UidFor("uid-1", "account", 10));
    }

    [Theory]
    [InlineData("https://jobs.example.invalid/careers", "example.invalid", true)]
    [InlineData("example.invalid", "mail.example.invalid", true)]
    [InlineData("https://example.invalid/other.invalid/jobs", "other.invalid", false)]
    [InlineData("https://notexample.invalid", "example.invalid", false)]
    [InlineData("https://example.invalid.evil.invalid", "example.invalid", false)]
    [InlineData("mailto:example.invalid", "example.invalid", false)]
    public void Corporate_email_matching_uses_host_boundaries_not_path_substrings(string site, string domain, bool matches) =>
        Assert.Equal(matches, EmailInboxSources.CorporateDomain(site, domain));
}
