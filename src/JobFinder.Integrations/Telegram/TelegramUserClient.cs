using System.Globalization;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Storage;
using TL;
using WTelegram;

namespace JobFinder.Integrations.Telegram;

public sealed class TelegramOptions
{
    public const string Section = "Telegram";

    public bool Enabled { get; set; } = true;

    public int ApiId { get; set; }

    public string ApiHash { get; set; } = "";

    /// <summary>International format, e.g. +375291234567. Needed for the first login only.</summary>
    public string Phone { get; set; } = "";

    /// <summary>Cloud password (2FA), if the account has one. Needed for the first login only.</summary>
    public string Password { get; set; } = "";

    /// <summary>--login-telegram also polls this file for the confirmation code (so the code can be supplied from outside).</summary>
    public string CodeFile { get; set; } = "data/telegram.code";

    /// <summary>Chat folder all monitored channels are put in; chats added to it by hand are monitored too.</summary>
    public string FolderTitle { get; set; } = "Jobs";

    public string SeedFile { get; set; } = "config/telegram-channels.json";

    public int JoinDelayMinSec { get; set; } = 40;

    public int JoinDelayMaxSec { get; set; } = 120;

    /// <summary>Joins per sync run: Telegram limits how fast an account may join channels.</summary>
    public int MaxJoinsPerRun { get; set; } = 8;

    /// <summary>History fetched per chat on start (messages newer than the last one seen).</summary>
    public int CatchUpLimit { get; set; } = 50;
}

/// <param name="Links">What the text does not show: text links, URL buttons, mentions of people (<see cref="TelegramLinks"/>).</param>
public sealed record TgPost(long ChatId, string ChatTitle, string? ChatUsername, int MessageId, string Text, DateTimeOffset Date, string? Author, string Url,
    IReadOnlyList<PostLink> Links);

public sealed record TgChatInfo(long Id, long AccessHash, string Title, string? Username, ChannelKind Kind, bool IsMember);

/// <summary>An incoming private message (a recruiter answering a DM).</summary>
public sealed record TgPrivateMessage(long UserId, string? Username, string? Name, int MessageId, string Text, DateTimeOffset Date);

/// <summary>A Telegram account or chat found by name (contact discovery).</summary>
public sealed record TgPeople(long Id, string? Username, string Name, bool IsChannel);

/// <summary>A message of a private chat: ours or theirs; a file shows as «📎 name», with its caption under it.</summary>
public sealed record TgDirectMessage(int Id, bool Mine, string Text, DateTimeOffset Date);

/// <summary>The private chat with a person, oldest message first.</summary>
public sealed record TgDirectHistory(long UserId, string? Username, string Name, List<TgDirectMessage> Messages);

/// <summary>Needed input during an interactive login ("verification_code", "password").</summary>
public delegate Task<string?> LoginPrompt(string what);

/// <summary>
/// The user account over MTProto (WTelegramClient). Channels and supergroups only: basic groups have no stable public identity and
/// job chats are supergroups anyway. All methods expect a logged-in session; <see cref="ConnectAsync"/> never prompts.
/// </summary>
/// <summary>What a @username is, for code that must not depend on the MTProto types (and for tests).</summary>
public interface ITelegramUsernames
{
    bool IsReady { get; }

    /// <exception cref="TelegramFloodException">Telegram asks to wait before resolving again.</exception>
    Task<(TelegramPeerKind Kind, string? Name)> ResolveKindAsync(string username);
}

/// <summary>Telegram answered FLOOD_WAIT: no more resolving before <see cref="RetryAfter"/> has passed.</summary>
public sealed class TelegramFloodException(TimeSpan retryAfter) : Exception($"Telegram asks to wait {retryAfter.TotalSeconds:0} s")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}

/// <remarks>
/// The session lives in the database (<see cref="ITelegramSessionStore"/>), the update-manager state in the document
/// <see cref="UpdatesKey"/>: on restart, updates missed while the app was down are fetched from it.
/// </remarks>
public sealed class TelegramUserClient(TelegramOptions options, ITelegramSessionStore sessions, IDocumentStore documents, ILogger<TelegramUserClient> log)
    : IAsyncDisposable, ITelegramUsernames
{
    public const string UpdatesKey = "telegram.updates";

    private static readonly TimeSpan SessionFlushTimeout = TimeSpan.FromSeconds(15);

    private Client? _client;
    private SessionWriter? _sessionWriter;
    private UpdateManager? _updates;
    private Func<TgPost, Task>? _onPost;
    private Func<TgPrivateMessage, Task>? _onPrivate;
    private Func<long, bool>? _isMonitored;

    public bool IsReady => _client is not null;

    public long? SelfId { get; private set; }

    static TelegramUserClient()
    {
        // WTelegram logs every MTProto frame at level 1-2; keep it quiet unless something is wrong.
        Helpers.Log = (level, text) =>
        {
            if (level >= 3) Console.Error.WriteLine($"[wtelegram] {text}");
        };
    }

    public async Task<bool> HasSessionAsync(CancellationToken ct) => await sessions.LoadAsync(ct) is { Length: > 0 };

    /// <summary>Resumes the saved session. Returns false (and logs why) when a login is needed: the service does not prompt.</summary>
    public async Task<bool> ConnectAsync(CancellationToken ct)
    {
        if (_client is not null) return true;
        if (options.ApiId == 0 || string.IsNullOrWhiteSpace(options.ApiHash))
        {
            log.Warn("[telegram] Telegram:ApiId/ApiHash are not set, the Telegram source is off");
            return false;
        }

        var session = await sessions.LoadAsync(ct);
        if (session is not { Length: > 0 })
        {
            log.Warn("[telegram] no session in the database: run --login-telegram once");
            return false;
        }

        var client = CreateClient(session);
        var next = await client.Login(options.Phone);
        if (next is not null)
        {
            log.Error($"[telegram] the saved session is not valid (asks for {next}): run --login-telegram again");
            await client.DisposeAsync();
            await FlushSessionAsync();
            return false;
        }

        _client = client;
        SelfId = client.User?.id;
        log.Info($"[telegram] connected as {client.User?.MainUsername ?? client.User?.first_name} ({SelfId})");
        return true;
    }

    /// <summary>Interactive first login (CLI): asks for the code (and the 2FA password when not in config).</summary>
    public async Task<string> LoginInteractiveAsync(LoginPrompt prompt, CancellationToken ct)
    {
        // An existing session is continued (a login that stopped half-way), as WTelegramClient does with its file.
        var client = CreateClient(await sessions.LoadAsync(ct));
        var what = await client.Login(options.Phone);
        while (what is not null)
        {
            ct.ThrowIfCancellationRequested();
            var value = what switch
            {
                "phone_number" when !string.IsNullOrWhiteSpace(options.Phone) => options.Phone,
                "password" when !string.IsNullOrWhiteSpace(options.Password) => options.Password,
                _ => await prompt(what),
            };
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"login aborted: no {what}");
            what = await client.Login(value);
        }

        _client = client;
        SelfId = client.User?.id;
        return $"{client.User?.first_name} {client.User?.last_name} (@{client.User?.MainUsername}, id {SelfId})";
    }

    /// <summary>Starts the live update stream. Messages of chats for which <paramref name="isMonitored"/> is true reach <paramref name="onPost"/>.</summary>
    public async Task StartListeningAsync(Func<long, bool> isMonitored, Func<TgPost, Task> onPost, Func<TgPrivateMessage, Task>? onPrivate,
        CancellationToken ct)
    {
        var client = Require();
        _isMonitored = isMonitored;
        _onPost = onPost;
        _onPrivate = onPrivate;
        _updates = client.WithUpdateManager(OnUpdateAsync, await LoadUpdatesStateAsync(ct));
    }

    /// <summary>The update-manager state as WTelegramClient reads and writes it (its own JSON options); null = start from now.</summary>
    private async Task<IDictionary<long, UpdateManager.MBoxState>?> LoadUpdatesStateAsync(CancellationToken ct)
    {
        if (await documents.GetAsync(UpdatesKey, ct) is not { } stored) return null;
        try
        {
            var state = JsonSerializer.Deserialize<Dictionary<long, UpdateManager.MBoxState>>(stored.Json, Helpers.JsonOptions);
            log.Info($"[telegram] updates state v{stored.Version} ({state?.Count ?? 0} boxes, saved {stored.UpdatedAt:u}): missed updates are fetched");
            return state;
        }
        catch (JsonException e)
        {
            log.Warn($"[telegram] updates state v{stored.Version} does not parse, starting from now: {e.Message}");
            return null;
        }
    }

    public async Task SaveUpdatesStateAsync(CancellationToken ct)
    {
        if (_updates is null) return;
        try
        {
            await documents.SaveAsync(UpdatesKey, JsonSerializer.Serialize(_updates.State, Helpers.JsonOptions), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.Warn($"[telegram] updates state not saved: {e.Message}");
        }
    }

    public async Task<TgChatInfo?> ResolveAsync(string username)
    {
        var resolved = await Require().Contacts_ResolveUsername(username.TrimStart('@'));
        return resolved.Chat is Channel channel ? Info(channel) : null;
    }

    public async Task<TgChatInfo?> GetChatAsync(long id, long accessHash)
    {
        var result = await Require().Channels_GetChannels(new InputChannel(id, accessHash));
        return result.chats.Values.OfType<Channel>().SingleOrDefault(channel => channel.id == id) is { } chat ? Info(chat) : null;
    }

    public async Task JoinChannelAsync(long id, long accessHash)
    {
        if (await Require().Channels_JoinChannel(new InputChannel(id, accessHash)) is null)
            throw new InvalidOperationException("Telegram join returned no receipt");
    }

    public async Task MuteChannelAsync(long id, long accessHash)
    {
        var applied = await Require().Account_UpdateNotifySettings(new InputNotifyPeer { peer = new InputPeerChannel(id, accessHash) }, new InputPeerNotifySettings
        {
            flags = InputPeerNotifySettings.Flags.has_mute_until,
            mute_until = DateTime.UnixEpoch.AddSeconds(int.MaxValue),
        });
        if (!applied) throw new InvalidOperationException("Telegram mute returned a negative receipt");
    }

    public async Task ArchiveChannelAsync(long id, long accessHash)
    {
        if (await Require().Folders_EditPeerFolders(new InputFolderPeer { peer = new InputPeerChannel(id, accessHash), folder_id = 1 }) is null)
            throw new InvalidOperationException("Telegram archive returned no receipt");
    }

    public Task AddChannelToFolderAsync(long id, long accessHash, string folder, Func<Task> verify) => AddToFolderAsync(new InputPeerChannel(id, accessHash), folder, verify);

    /// <summary>
    /// What a @username is — a person, a channel, a group, a bot, or nobody — and its name or title. One contacts.resolveUsername call;
    /// a flood wait surfaces as <see cref="RpcException"/> (the caller keeps the handle unverified rather than guessing).
    /// </summary>
    public async Task<(TelegramPeerKind Kind, string? Name)> ResolveKindAsync(string username)
    {
        var key = username.Trim().TrimStart('@');
        try
        {
            var resolved = await Require().Contacts_ResolveUsername(key);
            return resolved.UserOrChat switch
            {
                User { IsBot: true } bot => (TelegramPeerKind.Bot, bot.first_name),
                User u => (TelegramPeerKind.Person, $"{u.first_name} {u.last_name}".Trim()),
                Channel { IsChannel: true } c => (TelegramPeerKind.Channel, c.title),
                ChatBase chat => (TelegramPeerKind.Group, chat.Title),
                _ => (TelegramPeerKind.Missing, null),
            };
        }
        catch (RpcException e) when (e.Message is "USERNAME_NOT_OCCUPIED" or "USERNAME_INVALID")
        {
            return (TelegramPeerKind.Missing, null);
        }
        catch (RpcException e) when (e.Code == 420)
        {
            throw new TelegramFloodException(TimeSpan.FromSeconds(Math.Max(1, e.X)));
        }
    }

    /// <summary>A @username that belongs to a person (not a channel, a group or a bot); null otherwise or when it does not exist.</summary>
    public async Task<TgPeople?> ResolvePersonAsync(string username) =>
        await PersonAsync(username) is { } u ? new TgPeople(u.id, u.MainUsername, $"{u.first_name} {u.last_name}".Trim(), false) : null;

    /// <summary>
    /// The private chat with a person, both sides (messages written from the phone too): the recruiter's conversation for the funnel.
    /// null when the username is not a person.
    /// </summary>
    public async Task<TgDirectHistory?> GetDirectHistoryAsync(string username, int limit = 60)
    {
        if (await PersonAsync(username) is not { } user) return null;
        var history = await Require().Messages_GetHistory(user, limit: limit);
        var messages = history.Messages.OfType<Message>()
            .Select(m => new TgDirectMessage(m.id, m.flags.HasFlag(Message.Flags.out_), Describe(m), new DateTimeOffset(DateTime.SpecifyKind(m.date, DateTimeKind.Utc))))
            .Where(m => m.Text.Length > 0).OrderBy(m => m.Id).ToList();
        return new TgDirectHistory(user.id, user.MainUsername, $"{user.first_name} {user.last_name}".Trim(), messages);
    }

    private static string Describe(Message m)
    {
        var media = m.media switch
        {
            MessageMediaDocument { document: Document d } => "📎 " + (d.Filename ?? "файл"),
            MessageMediaPhoto => "🖼 фото",
            null => null,
            _ => "📎 вложение",
        };
        return media is null ? m.message ?? "" : string.IsNullOrWhiteSpace(m.message) ? media : media + "\n" + m.message;
    }

    /// <summary>
    /// Usernames resolve once per process: Telegram rate-limits contacts.resolveUsername hard, and the user id and access hash of a person
    /// do not change.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, User?> _people = new(StringComparer.OrdinalIgnoreCase);

    private async Task<User?> PersonAsync(string username)
    {
        var key = username.Trim().TrimStart('@');
        if (_people.TryGetValue(key, out var cached)) return cached;
        try
        {
            var resolved = await Require().Contacts_ResolveUsername(key);
            var user = resolved.User is { IsBot: false } u ? u : null;
            _people[key] = user;
            return user;
        }
        catch (RpcException e) when (e.Message is "USERNAME_NOT_OCCUPIED" or "USERNAME_INVALID")
        {
            _people[key] = null;
            return null;
        }
    }

    public async Task<List<TgPost>> GetHistoryAsync(long id, long accessHash, string title, string? username, int minId, int limit)
    {
        var history = await Require().Messages_GetHistory(new InputPeerChannel(id, accessHash), limit: limit, min_id: minId);
        return history.Messages.OfType<Message>()
            .Where(m => !string.IsNullOrWhiteSpace(m.message))
            .Select(m => ToPost(m, id, title, username, history.UserOrChat(m.From ?? m.Peer), u => history.UserOrChat(new PeerUser { user_id = u }) as User))
            .OrderBy(p => p.MessageId)
            .ToList();
    }

    /// <summary>One post of a channel as it is now (its text links and buttons included), or null when it is gone or empty.</summary>
    public async Task<TgPost?> GetPostAsync(long id, long accessHash, string title, string? username, int messageId)
    {
        var result = await Require().Channels_GetMessages(new InputChannel(id, accessHash), new InputMessageID { id = messageId });
        return result.Messages.OfType<Message>().FirstOrDefault(m => m.id == messageId && !string.IsNullOrWhiteSpace(m.message)) is { } m
            ? ToPost(m, id, title, username, result.UserOrChat(m.From ?? m.Peer), u => result.UserOrChat(new PeerUser { user_id = u }) as User)
            : null;
    }

    /// <summary>Channels and supergroups in the monitoring folder (including ones the user put there by hand).</summary>
    public async Task<List<TgChatInfo>> GetFolderChatsAsync()
    {
        var client = Require();
        var folder = await FindFolderAsync();
        if (folder is null) return [];
        var ids = folder.include_peers.OfType<InputPeerChannel>().Select(p => p.channel_id).ToHashSet();
        var all = await client.Messages_GetAllChats();
        return all.chats.Values.OfType<Channel>().Where(c => ids.Contains(c.id)).Select(Info).ToList();
    }

    /// <summary>
    /// People and channels by name: the company's HR accounts and channels for contact discovery. Telegram's search matches names and
    /// usernames only, so this is a hint list, not an authority.
    /// </summary>
    public async Task<List<TgPeople>> SearchAsync(string query, int limit = 20)
    {
        var found = await Require().Contacts_Search(query, limit);
        var people = found.users.Values.OfType<User>().Where(u => !u.IsBot)
            .Select(u => new TgPeople(u.id, u.MainUsername, $"{u.first_name} {u.last_name}".Trim(), false));
        var chats = found.chats.Values.OfType<Channel>().Select(c => new TgPeople(c.id, c.MainUsername, c.title, true));
        return people.Concat(chats).ToList();
    }

    /// <summary>Target of <see cref="SendDirectMessageAsync"/> that means the account's own "Saved Messages" (test sends).</summary>
    public const string SavedMessages = "me";

    public Task<long?> SendToSavedMessagesAsync(string text, string? attachmentPath = null, string? attachmentName = null, byte[]? attachmentContent = null) =>
        SendDirectMessageAsync(SavedMessages, text, attachmentPath, attachmentName, attachmentContent);

    /// <summary>
    /// A DM to a recruiter. With an attachment it is one message — the document with the text as its caption — unless the text exceeds
    /// the caption limit, then the text goes first and the document right after it.
    /// </summary>
    /// <returns>The recipient's user id (replies are matched by it), null for a channel or Saved Messages.</returns>
    public async Task<long?> SendDirectMessageAsync(string username, string text, string? attachmentPath = null, string? attachmentName = null,
        byte[]? attachmentContent = null)
    {
        const int captionLimit = 1000;
        var client = Require();
        InputPeer peer;
        long? userId = null;
        if (username == SavedMessages)
        {
            peer = InputPeer.Self;
        }
        else if (await PersonAsync(username) is { } person)
        {
            userId = person.id;
            peer = person;
        }
        else
        {
            var resolved = await client.Contacts_ResolveUsername(username.TrimStart('@'));
            peer = resolved.Chat is { } chat ? chat : throw new InvalidOperationException($"@{username.TrimStart('@')} не найден или это бот");
        }

        if (attachmentPath is null && attachmentContent is null)
        {
            await client.SendMessageAsync(peer, text);
            return userId;
        }

        await using Stream stream = attachmentContent is { } content ? new MemoryStream(content, writable: false) : File.OpenRead(attachmentPath!);
        var fileName = attachmentName ?? Path.GetFileName(attachmentPath ?? "resume.pdf");
        var file = await client.UploadFileAsync(stream, fileName);
        var mime = Path.GetExtension(fileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            ? "application/pdf"
            : "application/octet-stream";
        if (text.Length <= captionLimit)
        {
            await client.SendMediaAsync(peer, text, file, mime);
            return userId;
        }

        await client.SendMessageAsync(peer, text);
        await client.SendMediaAsync(peer, "", file, mime);
        return userId;
    }

    /// <summary>
    /// Writes the updates state and the last session, then closes the connection — while the database is still there: the host's stopping
    /// phase (<c>TelegramService.StopAsync</c>) or the end of a CLI command. By the time singletons are disposed the service provider is gone
    /// and nothing reaches the database any more. Safe to call twice.
    /// </summary>
    public async Task ShutdownAsync()
    {
        await SaveUpdatesStateAsync(CancellationToken.None);
        _updates = null;
        if (_client is not null) await _client.DisposeAsync();
        _client = null;
        await FlushSessionAsync();
    }

    public async ValueTask DisposeAsync() => await ShutdownAsync();

    /// <summary>
    /// A client over the stored session (WTelegramClient's own byte[] + callback constructor: no file). The session is decrypted here, so a
    /// wrong api_hash or a damaged session fails now, before any connection.
    /// </summary>
    private Client CreateClient(byte[]? session)
    {
        var writer = new SessionWriter(sessions, log);
        var client = new Client(Config(options), session, writer.Enqueue);
        _sessionWriter = writer;
        return client;
    }

    private static Func<string, string?> Config(TelegramOptions options) => what => what switch
    {
        "api_id" => options.ApiId.ToString(CultureInfo.InvariantCulture),
        "api_hash" => options.ApiHash,
        "phone_number" => string.IsNullOrWhiteSpace(options.Phone) ? null : options.Phone,
        _ => null,
    };

    /// <summary>
    /// Checks that <paramref name="session"/> is a session this account's api_hash opens (offline: WTelegramClient decrypts it and checks
    /// its hash, nothing connects). Used before an old session file is taken into the database.
    /// </summary>
    /// <exception cref="WTException">Not a session of this api_hash, or damaged.</exception>
    public static void ValidateSession(TelegramOptions options, byte[] session)
    {
        using var client = new Client(Config(options), session, _ => { });
    }

    private async Task FlushSessionAsync()
    {
        if (_sessionWriter is { } writer) await writer.FlushAsync(SessionFlushTimeout);
    }

    private Client Require() => _client ?? throw new InvalidOperationException("Telegram client is not connected");

    private async Task OnUpdateAsync(Update update)
    {
        if (update is UpdateNewMessage { message: Message { peer_id: PeerUser { user_id: var userId } } dm } && !dm.flags.HasFlag(Message.Flags.out_)
            && _onPrivate is not null && !string.IsNullOrWhiteSpace(dm.message))
        {
            var sender = _updates?.UserOrChat(dm.peer_id) as User;
            try
            {
                await _onPrivate(new TgPrivateMessage(userId, sender?.MainUsername, sender is null ? null : $"{sender.first_name} {sender.last_name}".Trim(),
                    dm.id, dm.message, new DateTimeOffset(DateTime.SpecifyKind(dm.date, DateTimeKind.Utc))));
            }
            catch (Exception e)
            {
                log.Error($"[telegram] private message {userId}/{dm.id} not handled: {e.Message}", e);
            }

            return;
        }

        if (update is not UpdateNewMessage { message: Message message } || _onPost is null || _isMonitored is null) return;
        if (message.peer_id is not PeerChannel { channel_id: var chatId } || !_isMonitored(chatId)) return;
        if (string.IsNullOrWhiteSpace(message.message)) return;

        var chat = _updates?.UserOrChat(message.peer_id) as Channel;
        var author = message.From is { } from ? _updates?.UserOrChat(from) : null;
        try
        {
            await _onPost(ToPost(message, chatId, chat?.title ?? chatId.ToString(CultureInfo.InvariantCulture), chat?.MainUsername, author,
                u => _updates?.UserOrChat(new PeerUser { user_id = u }) as User));
        }
        catch (Exception e)
        {
            log.Error($"[telegram] post {chatId}/{message.id} not stored: {e.Message}", e);
        }
    }

    private async Task<DialogFilter?> FindFolderAsync()
    {
        var filters = await Require().Messages_GetDialogFilters();
        return filters.filters.OfType<DialogFilter>().FirstOrDefault(f => f.title?.text == options.FolderTitle);
    }

    private async Task AddToFolderAsync(InputPeerChannel peer, string title, Func<Task> verify)
    {
        var client = Require();
        var filters = await client.Messages_GetDialogFilters();
        var folderTitle = title;
        var folder = filters.filters.OfType<DialogFilter>().FirstOrDefault(f => f.title?.text == folderTitle);
        if (folder is null)
        {
            var used = filters.filters.OfType<DialogFilterBase>().Select(f => f.ID).ToHashSet();
            var id = Enumerable.Range(2, 250).First(i => !used.Contains(i));
            folder = new DialogFilter
            {
                id = id,
                title = new TextWithEntities { text = folderTitle, entities = [] },
                include_peers = [],
                pinned_peers = [],
                exclude_peers = [],
            };
        }

        if (folder.include_peers.OfType<InputPeerChannel>().Any(p => p.channel_id == peer.channel_id)) return;
        folder.include_peers = [.. folder.include_peers, peer];
        await verify();
        if (!await client.Messages_UpdateDialogFilter(folder.id, folder))
            throw new InvalidOperationException("Telegram folder update returned a negative receipt");
    }

    private static TgChatInfo Info(Channel c) =>
        new(c.id, c.access_hash, c.title, c.MainUsername, c.IsChannel ? ChannelKind.Channel : ChannelKind.Group, !c.flags.HasFlag(Channel.Flags.left));

    private static TgPost ToPost(Message m, long chatId, string title, string? username, IPeerInfo? author, Func<long, User?> users)
    {
        var url = username is not null ? $"https://t.me/{username}/{m.id}" : $"https://t.me/c/{chatId}/{m.id}";
        var authorName = author switch
        {
            User u => u.MainUsername is { } un ? "@" + un : $"{u.first_name} {u.last_name}".Trim(),
            ChatBase c => c.Title,
            _ => null,
        };
        return new TgPost(chatId, title, username, m.id, m.message, new DateTimeOffset(DateTime.SpecifyKind(m.date, DateTimeKind.Utc)), authorName, url,
            TelegramLinks.Extract(m, users));
    }
}
