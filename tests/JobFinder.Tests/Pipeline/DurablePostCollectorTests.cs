using System.Reflection;
using JobFinder.Core.Domain;
using JobFinder.Integrations.Telegram;
using JobFinder.Integrations.Threads;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Modules.Discovery;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurablePostProcessingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_source_admission_commits_one_native_root_and_preserves_duplicate_counters(bool telegram)
    {
        var steps = new Steps();
        var producer = await HostAsync(false, steps, configure: services =>
        {
            services.RemoveAll<IChannelMembershipGateway>();
            services.AddSingleton<IChannelMembershipGateway, CollectorAccount>();
        });
        if (telegram) producer.Services.GetRequiredService<ServerOwnership>().Acquired();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.TelegramChannels.Add(new()
        {
            Username = "synthetic", Title = "Synthetic vacancies", Kind = ChannelKind.Channel, Status = ChannelStatus.Joined,
            PeerId = 987654, LastMessageId = 100, AddedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync(Ct);
        using var collector = telegram
            ? (Microsoft.Extensions.Hosting.BackgroundService)ActivatorUtilities.CreateInstance<TelegramService>(producer.Services)
            : ActivatorUtilities.CreateInstance<ThreadsJob>(producer.Services);
        object post = telegram
            ? new TgPost(987654, "Synthetic vacancies", "synthetic", 101, NewPost().Text, DateTimeOffset.UtcNow, "recruiter", "https://t.me/synthetic/101", [])
            : new ThreadsPost("101", "synthetic", NewPost().Text, DateTimeOffset.UtcNow, "https://www.threads.net/@synthetic/post/101");
        var method = collector.GetType().GetMethod(telegram ? "StoreAsync" : "StorePostAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True(await (Task<bool>)method.Invoke(collector, telegram ? [post, 42L, Ct] : [post, Ct])!);
        Assert.False(await (Task<bool>)method.Invoke(collector, telegram ? [post, 42L, Ct] : [post, Ct])!);
        var raw = Assert.Single(await context.RawPosts.AsNoTracking().ToListAsync(Ct));
        var root = Assert.Single(await context.PostProcessingRequests.AsNoTracking().ToListAsync(Ct));
        Assert.Equal(root.Id, raw.ProcessingRequestId);
        Assert.Equal(PostProcessingStage.Reading, root.Stage);
        var channel = await context.TelegramChannels.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(telegram ? 1 : 0, channel.PostsSeen);
        Assert.Equal(telegram ? 101 : 100, channel.LastMessageId);
        Assert.Equal(0, steps.Extractions);
        await producer.StopAsync(Ct);
        await HostAsync(true, steps);
        await UntilAsync(async () => (await RootAsync(root.Id)).Stage == PostProcessingStage.Completed);
        Assert.Equal(1, steps.Extractions);
        Assert.Equal(telegram ? 1 : 0, (await context.TelegramChannels.AsNoTracking().SingleAsync(Ct)).VacanciesFound);
        Assert.Empty(await context.TelegramSessions.ToListAsync(Ct));
    }
    private sealed class CollectorAccount : IChannelMembershipGateway
    {
        public long? AccountId => 42;
        public Task<ChannelMembershipChat?> ResolveAsync(ChannelMembershipInput input, long accountId, CancellationToken ct) =>
            throw new InvalidOperationException("collection admission must not resolve membership");
        public Task ApplyAsync(ChannelMembershipStage stage, ChannelMembershipChat chat, string folder, long accountId,
            Func<CancellationToken, Task> verify, CancellationToken ct) =>
            throw new InvalidOperationException("collection admission must not execute membership effects");
        public Task<ChannelMembershipHistory> HistoryAsync(ChannelMembershipChat chat, long accountId, CancellationToken ct) =>
            throw new InvalidOperationException("collection admission uses its original observation");
    }

}
