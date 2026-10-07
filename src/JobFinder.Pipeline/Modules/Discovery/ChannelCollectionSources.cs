using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Integrations.Telegram;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Discovery;

public sealed record ChannelCollectionSnapshot(ChannelDecisionSnapshot Source, long? AccessHash, Guid? MembershipRequestId)
{
    public static ChannelCollectionSnapshot Capture(TelegramChannel source) => new(ChannelDecisionSnapshot.Capture(source), source.AccessHash, source.MembershipRequestId);
    public bool Matches(TelegramChannel source) => Source.Matches(source) && AccessHash == source.AccessHash && MembershipRequestId == source.MembershipRequestId;
}

public sealed record ChannelFolderCapture(long AccountId, IReadOnlyList<ChannelCollectionSnapshot> Sources);
public sealed record ChannelContactBackfillCapture(long AccountId, string? Value, DateTimeOffset? UpdatedAt)
{
    public bool Completed => Value == "true";
}

/// <summary>Publishes read-only MTProto observations with their original account and source witnesses.</summary>
public sealed class ChannelCollectionSources(IDbContextFactory<JobFinderDbContext> db, NativePublicationScopes scopes,
    IChannelMembershipGateway gateway, ServerOwnership ownership, Notifier notifier, ChannelMonitoring monitoring, TimeProvider time,
    PostProcessingRequests posts, JobFinder.Llm.Tasks.JobFinderLlm llm, SearchPolicyStore searches)
{
    public const string ContactsBackfilledKey = "contacts.fromDescriptions.v1";
    public static bool OwnsKey(string key) => key == ContactsBackfilledKey;

    public async Task<ChannelCollectionSnapshot?> CaptureSourceAsync(long peerId, long accountId, CancellationToken ct)
    {
        ownership.EnsureOwned();
        if (peerId <= 0 || accountId <= 0 || gateway.AccountId != accountId) return null;
        await using var context = await db.CreateDbContextAsync(ct);
        var source = await context.TelegramChannels.AsNoTracking().SingleOrDefaultAsync(row => row.PeerId == peerId && row.Status == ChannelStatus.Joined, ct);
        return source is null ? null : ChannelCollectionSnapshot.Capture(source);
    }

    public async Task<int> PublishHistoryAsync(ChannelCollectionSnapshot captured, long accountId, IReadOnlyList<TgPost> observations, CancellationToken ct)
    {
        ownership.EnsureOwned();
        if (accountId <= 0 || gateway.AccountId != accountId || captured.Source.Status != ChannelStatus.Joined || captured.Source.PeerId is not > 0
            || observations.Any(post => post.ChatId != captured.Source.PeerId || post.MessageId <= 0 || string.IsNullOrWhiteSpace(post.Text) || post.Links is null || post.Date == default)
            || observations.Select(post => post.MessageId).Distinct().Count() != observations.Count) return 0;
        var operation = await llm.ForOperationAsync(ct);
        var search = await searches.ReadAsync(ct);
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await ChannelMembershipRequests.LockAsync(context, ct);
        var source = await context.TelegramChannels.SingleOrDefaultAsync(row => row.Id == captured.Source.Id, ct);
        if (source is null || !captured.Matches(source)) return 0;
        var count = 0;
        var chatRef = captured.Source.PeerId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var observed in observations)
        {
            var externalId = observed.MessageId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (await context.RawPosts.AnyAsync(row => row.Source == SourceKind.Telegram && row.ChatRef == chatRef && row.ExternalId == externalId, ct))
            {
                await posts.AdvanceTelegramCursorJoinedAsync(context, source.Id, captured.Source.PeerId.Value, observed.MessageId, ct);
                continue;
            }
            await posts.StoreJoinedAsync(context, outbox, new RawPost { Source = SourceKind.Telegram, ChatRef = chatRef, ChatTitle = observed.ChatTitle,
                ExternalId = externalId, Url = observed.Url, Author = observed.Author, Text = observed.Text, Links = [.. observed.Links],
                PublishedAt = observed.Date, FetchedAt = time.GetUtcNow(), Status = RawPostStatus.New }, operation, search, ct);
            count++;
        }
        ownership.EnsureOwned();
        if (gateway.AccountId != accountId) return 0;
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return count;
    }

    public async Task<ChannelContactBackfillCapture?> CaptureContactsBackfillAsync(CancellationToken ct)
    {
        ownership.EnsureOwned();
        var account = gateway.AccountId;
        if (account is not > 0) return null;
        await using var context = await db.CreateDbContextAsync(ct);
        var row = await context.Settings.AsNoTracking().SingleOrDefaultAsync(row => row.Key == ContactsBackfilledKey, ct);
        if (row is not null && row.Value is not ("true" or "false")) throw new UnsupportedChannelMembershipException();
        return new(account.Value, row?.Value, row?.UpdatedAt);
    }

    public async Task<bool> CompleteContactsBackfillAsync(ChannelContactBackfillCapture captured, CancellationToken ct)
    {
        ownership.EnsureOwned();
        if (captured.AccountId <= 0 || gateway.AccountId != captured.AccountId || captured.Value is not (null or "true" or "false")) return false;
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await ChannelMembershipRequests.LockAsync(context, ct);
        var row = await context.Settings.SingleOrDefaultAsync(row => row.Key == ContactsBackfilledKey, ct);
        if (row?.Value != captured.Value || row?.UpdatedAt != captured.UpdatedAt) return false;
        if (captured.Completed) return true;
        if (row is null) { row = new AppSetting { Key = ContactsBackfilledKey, Value = "true" }; context.Settings.Add(row); }
        row.Value = "true";
        row.UpdatedAt = time.GetUtcNow();
        notifier.Add(context, NotificationKind.Info, "✅ Telegram: первичная проверка контактов в сохранённых описаниях завершена.");
        ownership.EnsureOwned();
        if (gateway.AccountId != captured.AccountId) return false;
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return true;
    }

    public async Task<ChannelFolderCapture?> CaptureFolderAsync(CancellationToken ct)
    {
        ownership.EnsureOwned();
        var identity = gateway.AccountId;
        if (identity is not > 0) return null;
        var account = identity.Value;
        await using var context = await db.CreateDbContextAsync(ct);
        var sources = await context.TelegramChannels.AsNoTracking().OrderBy(row => row.Id).ToListAsync(ct);
        return new(account, sources.Select(ChannelCollectionSnapshot.Capture).ToArray());
    }

    public async Task<ActionResult> PublishFolderAsync(ChannelFolderCapture captured, IReadOnlyList<TgChatInfo> observations, CancellationToken ct)
    {
        ownership.EnsureOwned();
        if (captured.AccountId <= 0 || gateway.AccountId != captured.AccountId || observations.Any(chat => chat.Id <= 0 || !chat.IsMember || !Enum.IsDefined(chat.Kind))
            || observations.Select(chat => chat.Id).Distinct().Count() != observations.Count
            || observations.Where(chat => chat.Username is not null).Select(chat => chat.Username).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != observations.Count(chat => chat.Username is not null)) return Conflict();
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await ChannelMembershipRequests.LockAsync(context, ct);
        var sources = await context.TelegramChannels.ToListAsync(ct);
        var joined = 0;
        foreach (var chat in observations)
        {
            var prior = captured.Sources.Where(row => row.Source.PeerId == chat.Id || chat.Username is not null && string.Equals(row.Source.Username, chat.Username, StringComparison.OrdinalIgnoreCase)).ToArray();
            var current = sources.Where(row => row.PeerId == chat.Id || chat.Username is not null && string.Equals(row.Username, chat.Username, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (prior.Length > 1 || current.Length > 1 || (prior.Length == 0) != (current.Length == 0)
                || prior.Length == 1 && !prior[0].Matches(current[0])) return Conflict();
            TelegramChannel source;
            if (current.Length == 0)
            {
                source = new TelegramChannel { Username = chat.Username, AddedAt = time.GetUtcNow(), Reason = "добавлен в папку вручную" };
                context.TelegramChannels.Add(source);
            }
            else
            {
                source = current[0];
                if (source.Status == ChannelStatus.Rejected || source.MembershipRequestId is not null
                    || await context.ChannelMembershipRequests.AnyAsync(root => root.ChannelId == source.Id, ct)) continue;
                var proof = await context.TelegramChannelDecisionRevisions.Where(row => row.ChannelId == source.Id).OrderByDescending(row => row.Revision).FirstOrDefaultAsync(ct);
                if (!ChannelDecisions.HasProof(source, proof)) return Conflict();
            }
            if (source.Status != ChannelStatus.Joined) joined++;
            source.PeerId = chat.Id;
            source.AccessHash = chat.AccessHash;
            source.Title = chat.Title;
            source.Username ??= chat.Username;
            source.Kind = chat.Kind;
            source.Status = ChannelStatus.Joined;
            source.JoinedAt ??= time.GetUtcNow();
        }
        ownership.EnsureOwned();
        if (gateway.AccountId != captured.AccountId) return Conflict();
        if (joined > 0) notifier.Add(context, NotificationKind.Info, "✅ Telegram: из папки принято каналов для мониторинга: " + joined);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return ActionResult.Done("наблюдения папки сохранены");
    }

    public async Task<bool> PublishAccessFailureAsync(ChannelCollectionSnapshot captured, long accountId, string reason, CancellationToken ct)
    {
        ownership.EnsureOwned();
        if (accountId <= 0 || gateway.AccountId != accountId || captured.Source.Status != ChannelStatus.Joined || captured.Source.PeerId is null) return false;
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await ChannelMembershipRequests.LockAsync(context, ct);
        var source = await context.TelegramChannels.SingleOrDefaultAsync(row => row.Id == captured.Source.Id, ct);
        if (source is null || !captured.Matches(source)) return false;
        source.Status = ChannelStatus.Failed;
        source.Error = reason;
        notifier.Add(context, NotificationKind.Alert, "⚠️ Telegram: " + JobFinder.Core.Text.TextUtil.EscapeHtml(source.Username ?? source.Title ?? source.Id.ToString()) + ": " + JobFinder.Core.Text.TextUtil.EscapeHtml(reason));
        ownership.EnsureOwned();
        if (gateway.AccountId != accountId) return false;
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        monitoring.Remove(captured.Source.PeerId.Value);
        return true;
    }

    private static ActionResult Conflict() => ActionResult.Fail("наблюдение Telegram устарело или повреждено; исходные решения сохранены");
}
