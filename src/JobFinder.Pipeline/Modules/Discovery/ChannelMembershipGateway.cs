using System.Globalization;
using JobFinder.Core.Domain;
using JobFinder.Integrations.Telegram;

namespace JobFinder.Pipeline.Modules.Discovery;

public interface IChannelMembershipGateway
{
    long? AccountId { get; }
    Task<ChannelMembershipChat?> ResolveAsync(ChannelMembershipInput input, long accountId, CancellationToken ct);
    Task ApplyAsync(ChannelMembershipStage stage, ChannelMembershipChat chat, string folder, long accountId, Func<CancellationToken, Task> verify, CancellationToken ct);
    Task<ChannelMembershipHistory> HistoryAsync(ChannelMembershipChat chat, long accountId, CancellationToken ct);
}

public sealed class ChannelMembershipGateway(TelegramUserClient client, TimeProvider time) : IChannelMembershipGateway
{
    public long? AccountId => client.IsReady ? client.SelfId : null;

    public async Task<ChannelMembershipChat?> ResolveAsync(ChannelMembershipInput input, long accountId, CancellationToken ct)
    {
        Ready(accountId);
        return await CallAsync(async () =>
        {
            var chat = input.Source.PeerId is { } id && input.AccessHash is { } hash
                ? await client.GetChatAsync(id, hash).WaitAsync(ct)
                : await client.ResolveAsync(input.Source.Username ?? throw new UnsupportedChannelMembershipException()).WaitAsync(ct);
            return chat is null ? null : new ChannelMembershipChat { Id = chat.Id, AccessHash = chat.AccessHash, Title = chat.Title,
                Username = chat.Username, Kind = chat.Kind, IsMember = chat.IsMember };
        });
    }

    public async Task ApplyAsync(ChannelMembershipStage stage, ChannelMembershipChat chat, string folder, long accountId, Func<CancellationToken, Task> verify, CancellationToken ct)
    {
        Ready(accountId);
        await CallAsync(async () =>
        {
            await verify(ct);
            Ready(accountId);
            var operation = stage switch
            {
                ChannelMembershipStage.Joining => client.JoinChannelAsync(chat.Id, chat.AccessHash),
                ChannelMembershipStage.Muting => client.MuteChannelAsync(chat.Id, chat.AccessHash),
                ChannelMembershipStage.Archiving => client.ArchiveChannelAsync(chat.Id, chat.AccessHash),
                ChannelMembershipStage.Filing => client.AddChannelToFolderAsync(chat.Id, chat.AccessHash, folder, async () => { await verify(ct); Ready(accountId); }),
                _ => throw new UnsupportedChannelMembershipException(),
            };
            await operation.WaitAsync(ct);
            return true;
        });
    }

    public async Task<ChannelMembershipHistory> HistoryAsync(ChannelMembershipChat chat, long accountId, CancellationToken ct)
    {
        Ready(accountId);
        return await CallAsync(async () => new ChannelMembershipHistory
        {
            Posts = (await client.GetHistoryAsync(chat.Id, chat.AccessHash, chat.Title, chat.Username, 0, 20).WaitAsync(ct))
                .Select(post => new RawPost { Source = SourceKind.Telegram, ChatRef = post.ChatId.ToString(CultureInfo.InvariantCulture),
                    ChatTitle = post.ChatTitle, ExternalId = post.MessageId.ToString(CultureInfo.InvariantCulture), Url = post.Url,
                    Author = post.Author, Text = post.Text, Links = [.. post.Links], PublishedAt = post.Date, FetchedAt = time.GetUtcNow(), Status = RawPostStatus.New }).ToList(),
        });
    }

    private void Ready(long accountId)
    {
        if (accountId <= 0 || AccountId != accountId) throw new ChannelSessionUnavailableException();
    }

    private static async Task<T> CallAsync<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (TL.RpcException error)
        {
            throw new ChannelMembershipRpcException(error.Message, error.Code == 420 ? TimeSpan.FromSeconds(Math.Max(60, error.X + 5)) : null,
                error.Message == "INVITE_REQUEST_SENT");
        }
    }
}
