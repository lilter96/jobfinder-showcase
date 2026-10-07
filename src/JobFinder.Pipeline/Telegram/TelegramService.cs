using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Integrations.Telegram;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Modules.Discovery;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace JobFinder.Pipeline.Telegram;

public sealed record ChannelSeed(string Username, string? Title, string? Kind, string? Reason);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(List<ChannelSeed>))]
internal sealed partial class SeedJsonContext : JsonSerializerContext;

/// <summary>
/// The Telegram source: keeps the account in the approved channels (join → mute → archive → folder, slowly), catches up on history
/// missed while the app was down, and turns every new message of a monitored chat into a <see cref="RawPost"/>.
/// Nothing is joined before the user approves it.
/// </summary>
public sealed class TelegramService(TelegramOptions options, TelegramUserClient client, IDbContextFactory<JobFinderDbContext> db,
    Career.ReplyService replies, Career.ContactRegistry contacts, Career.RecruiterChatService chats,
    JobTriggers triggers, Notifier notifier, ChannelDecisions channelDecisions, ChannelMembershipRequests memberships, ChannelMonitoring monitoring,
    TimeProvider time, ILogger<TelegramService> log, ChannelCollectionSources collectionSources) : BackgroundService
{

    /// <summary>The loop ends first, then the client writes its updates state and session while the database is still reachable.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await client.ShutdownAsync();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        triggers.Register(JobNames.TelegramSync);
        if (!options.Enabled) return;

        await SeedAsync(stoppingToken);
        await Task.Delay(TimeSpan.FromSeconds(5), time, stoppingToken);

        while (!await ConnectAsync(stoppingToken))
            await triggers.WaitAsync(JobNames.TelegramSync, TimeSpan.FromMinutes(10), time, stoppingToken);

        await ReloadMonitoredAsync(stoppingToken);
        var listeningAccount = client.SelfId ?? 0;
        await client.StartListeningAsync(monitoring.Contains, post => StoreAsync(post, listeningAccount, CancellationToken.None), OnPrivateAsync, stoppingToken);
        await ImportFolderAsync(stoppingToken);
        await CatchUpAsync(stoppingToken);
        await BackfillContactsAsync(stoppingToken);

        var lastStateSave = time.GetUtcNow();
        var lastChatSync = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await JoinApprovedAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                log.Error($"[telegram] channel sync failed: {e.Message}", e);
            }

            if (time.GetUtcNow() - lastStateSave > TimeSpan.FromMinutes(5))
            {
                await client.SaveUpdatesStateAsync(stoppingToken);
                lastStateSave = time.GetUtcNow();
            }

            // The recruiters' chats (both sides, the phone included) into the responses, every half hour.
            if (time.GetUtcNow() - lastChatSync > TimeSpan.FromMinutes(30))
            {
                lastChatSync = time.GetUtcNow();
                try
                {
                    await chats.SyncActiveAsync(stoppingToken);
                }
                catch (Exception e) when (!stoppingToken.IsCancellationRequested)
                {
                    log.Warn($"[telegram] recruiter chats: {e.Message}");
                }
            }

            await triggers.WaitAsync(JobNames.TelegramSync, TimeSpan.FromMinutes(10), time, stoppingToken);
        }
    }

    private async Task<bool> ConnectAsync(CancellationToken ct)
    {
        try
        {
            if (await client.ConnectAsync(ct)) return true;
        }
        catch (Exception e)
        {
            log.Error($"[telegram] connect failed: {e.Message}", e);
        }

        await notifier.AlertAsync("telegram-login", "Telegram-источник не подключён",
            "Нет рабочей сессии MTProto. Выполните: dotnet run --project src/JobFinder.App -- --login-telegram", TimeSpan.FromHours(12), ct);
        return false;
    }

    /// <summary>Seed file → Proposed rows (existing rows keep their status). New proposals are sent to the bot for approval.</summary>
    private async Task SeedAsync(CancellationToken ct)
    {
        if (!File.Exists(options.SeedFile)) return;
        List<ChannelSeed> seeds;
        try
        {
            seeds = JsonSerializer.Deserialize(await File.ReadAllTextAsync(options.SeedFile, ct), SeedJsonContext.Default.ListChannelSeed) ?? [];
        }
        catch (JsonException e)
        {
            log.Error($"[telegram] {options.SeedFile}: {e.Message}");
            return;
        }

        var added = await channelDecisions.ProposeAsync(seeds.Select(seed => new ChannelProposal(seed.Username, seed.Title,
            string.Equals(seed.Kind, "group", StringComparison.OrdinalIgnoreCase) ? ChannelKind.Group : ChannelKind.Channel, seed.Reason)).ToList(), ct);
        if (added > 0) log.Info($"[telegram] {added} channels proposed from {options.SeedFile}");
    }

    private async Task ReloadMonitoredAsync(CancellationToken ct)
    {
        var revision = monitoring.Revision;
        await using var ctx = await db.CreateDbContextAsync(ct);
        var ids = await ctx.TelegramChannels.Where(c => c.Status == ChannelStatus.Joined && c.PeerId != null).Select(c => c.PeerId!.Value).ToListAsync(ct);
        monitoring.Reload(ids, revision);
        log.Info($"[telegram] monitoring {monitoring.Count} chats");
    }

    /// <summary>Chats the user put into the folder by hand are monitored as well.</summary>
    private async Task ImportFolderAsync(CancellationToken ct)
    {
        var captured = await collectionSources.CaptureFolderAsync(ct);
        if (captured is null) return;
        List<TgChatInfo> chats;
        try
        {
            chats = await client.GetFolderChatsAsync();
        }
        catch (Exception e)
        {
            log.Warn($"[telegram] folder \"{options.FolderTitle}\" not read: {e.Message}");
            return;
        }

        var result = await collectionSources.PublishFolderAsync(captured, chats, ct);
        if (!result.Ok) log.Warn("[telegram] " + result.Message);
        await ReloadMonitoredAsync(ct);
    }

    /// <summary>
    /// Once: the recruiters' @usernames in the texts of vacancies collected before descriptions were read for them (new vacancies are read
    /// as they come). Here, not in a CLI: only this process may use the MTProto session, and it checks each username is a person.
    /// </summary>
    private async Task BackfillContactsAsync(CancellationToken ct)
    {
        var captured = await collectionSources.CaptureContactsBackfillAsync(ct);
        if (captured is null || captured.Completed) return;
        try
        {
            await using var ctx = await db.CreateDbContextAsync(ct);
            var candidates = await ctx.Vacancies.AsNoTracking()
                .Where(v => v.Description != null && (v.Description.Contains("@") || v.Description.Contains("t.me/") || v.Description.Contains("telegram.me/")))
                .ToListAsync(ct);
            var found = 0;
            foreach (var v in candidates.Where(v => Career.ContactRegistry.TelegramHandles(v.Description).Count > 0))
            {
                found += await contacts.FromDescriptionAsync(v, ct);
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(700, 1500)), time, ct);
            }

            if (!await collectionSources.CompleteContactsBackfillAsync(captured, ct))
                log.Warn("[telegram] contacts backfill completion conflicted; original progress was preserved");
            log.Info($"[telegram] recruiter contacts from {candidates.Count} older vacancy texts: {found}");
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.Warn($"[telegram] contacts backfill: {e.Message}");
        }
    }

    private async Task CatchUpAsync(CancellationToken ct)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        var channels = await ctx.TelegramChannels.Where(c => c.Status == ChannelStatus.Joined && c.PeerId != null && c.AccessHash != null).ToListAsync(ct);
        var stored = 0;
        foreach (var channel in channels)
        {
            var captured = ChannelCollectionSnapshot.Capture(channel);
            var accountId = client.SelfId ?? 0;
            try
            {
                var posts = await client.GetHistoryAsync(channel.PeerId!.Value, channel.AccessHash!.Value, channel.Title ?? "", channel.Username,
                    channel.LastMessageId, options.CatchUpLimit);
                var admitted = await collectionSources.PublishHistoryAsync(captured, accountId, posts, ct);
                stored += admitted;
                if (admitted > 0) triggers.Trigger(JobNames.Posts);
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(700, 1800)), time, ct);
            }
            catch (TL.RpcException e) when (e.Message is "CHANNEL_PRIVATE" or "CHANNEL_INVALID" or "USER_BANNED_IN_CHANNEL")
            {
                // Removed from the chat (a captcha bot kicks new members that do not answer it, or a ban): stop reading it and say why.
                var reason = $"нет доступа ({e.Message}): аккаунт удалён из чата — антиспам-капча или бан; вступите вручную и нажмите «Одобрить»";
                var published = await collectionSources.PublishAccessFailureAsync(captured, accountId, reason, ct);
                log.Warn($"[telegram] {channel.Username ?? channel.Title}: {e.Message}, "
                    + (published ? "monitoring stopped" : "source changed; access observation was not published"));
            }
            // WTelegram's own timeout is a TaskCanceledException too: only our shutdown may leave (it once stopped the whole host).
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.Warn($"[telegram] history of {channel.Username ?? channel.Title}: {e.Message}");
            }
        }

        await ReloadMonitoredAsync(ct);
        log.Info($"[telegram] catch-up: {stored} new posts from {channels.Count} chats");
    }

    private async Task JoinApprovedAsync(CancellationToken ct)
    {
        await memberships.ImportApprovedAsync(ct);
        await ReloadMonitoredAsync(ct);
    }

    /// <summary>A private message: a recruiter answering our DM or outreach goes to the funnel; everything else is not ours.</summary>
    private async Task OnPrivateAsync(TgPrivateMessage m)
    {
        try
        {
            await replies.HandleTelegramAsync(m, CancellationToken.None);
        }
        catch (Exception e)
        {
            log.Warn($"[telegram] private message from {m.Username ?? m.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture)}: {e.Message}");
        }
    }

    private async Task<bool> StoreAsync(TgPost post, long accountId, CancellationToken ct)
    {
        var captured = await collectionSources.CaptureSourceAsync(post.ChatId, accountId, ct);
        if (captured is null) return false;
        var count = await collectionSources.PublishHistoryAsync(captured, accountId, [post], ct);
        if (count > 0) triggers.Trigger(JobNames.Posts);
        return count > 0;
    }
}
