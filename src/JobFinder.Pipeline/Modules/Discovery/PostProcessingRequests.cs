using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Client;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Discovery;

public sealed class PostProcessingRequests(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes,
    SearchPolicyStore searches, JobFinderLlm llm, Notifier notifier, TimeProvider time)
{
    internal async Task AdvanceTelegramCursorJoinedAsync(JobFinderDbContext context, long sourceId, long peerId, int messageId, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is null || sourceId <= 0 || peerId <= 0 || messageId <= 0)
            throw new InvalidOperationException("Telegram cursor advance joins its captured source transaction");
        var chatRef = peerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var externalId = messageId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!await context.RawPosts.AnyAsync(row => row.Source == SourceKind.Telegram && row.ChatRef == chatRef && row.ExternalId == externalId, ct)) return;
        await context.TelegramChannels.Where(row => row.Id == sourceId && row.PeerId == peerId && row.Status == ChannelStatus.Joined).ExecuteUpdateAsync(update => update
            .SetProperty(row => row.LastMessageId, row => row.LastMessageId > messageId ? row.LastMessageId : messageId), ct);
    }

    public async Task StoreAsync(JobFinderDbContext context, RawPost post, CancellationToken ct)
    {
        var operation = await llm.ForOperationAsync(ct);
        var search = await searches.ReadAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        if (post.Source == SourceKind.Telegram) await ChannelMembershipRequests.LockAsync(context, ct);
        await StoreJoinedAsync(context, outbox, post, operation, search, ct);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    internal async Task StoreJoinedAsync(JobFinderDbContext context, IDbContextOutbox outbox, RawPost post, JobFinderLlm operation,
        SearchPolicySnapshot search, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is null) throw new InvalidOperationException("post admission requires its source transaction");
        context.RawPosts.Add(post);
        await notifier.SaveJoinedAsync(context, outbox, ct);
        // A captured policy can admit several history posts in one parent transaction. Each child owns its own operation ID.
        var child = operation.RestoreOperation(operation.CaptureOperation() with { OperationId = Guid.NewGuid() });
        await QueueAsync(context, outbox, post, child, search, false, ct);
        if (post.Source == SourceKind.Telegram && long.TryParse(post.ChatRef, out var peerId) && int.TryParse(post.ExternalId, out var messageId))
            await context.TelegramChannels.Where(row => row.PeerId == peerId).ExecuteUpdateAsync(update => update
                .SetProperty(row => row.PostsSeen, row => row.PostsSeen + 1)
                .SetProperty(row => row.LastMessageId, row => row.LastMessageId > messageId ? row.LastMessageId : messageId), ct);
    }

    public async Task<Guid?> RequestAsync(long postId, CancellationToken ct, bool refresh = false)
    {
        var operation = await llm.ForOperationAsync(ct);
        var search = await searches.ReadAsync(ct);
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var post = await context.RawPosts.FromSqlInterpolated($"SELECT * FROM raw_posts WHERE id = {postId} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (post is null || (refresh ? post.Status != RawPostStatus.Vacancy || post.VacancyId is null : post.Status != RawPostStatus.New)) return null;
        if (post.Source is not (SourceKind.Telegram or SourceKind.Threads))
        {
            post.Status = RawPostStatus.Failed;
            post.Error = "unsupported legacy post source";
            notifier.Add(context, NotificationKind.Alert, "⚠️ Пост #" + post.Id + ": " + post.Error);
            await notifier.SaveAndFlushAsync(context, ct);
            return null;
        }
        if (!refresh && post.ProcessingRequestId is { } existing)
        {
            if (await context.PostProcessingRequests.AnyAsync(row => row.Id == existing && row.PostId == post.Id && row.Revision == post.ProcessingRevision, ct)) return existing;
            post.Status = RawPostStatus.Failed;
            post.Error = "post processing owner is missing or mismatched";
            notifier.Add(context, NotificationKind.Alert, "⚠️ Пост #" + post.Id + ": " + post.Error);
            await notifier.SaveAndFlushAsync(context, ct);
            return null;
        }
        if (refresh && post.ProcessingRequestId is { } oldId)
        {
            var previous = await context.PostProcessingRequests.SingleOrDefaultAsync(row => row.Id == oldId, ct);
            if (previous is not null && previous.Stage is not (PostProcessingStage.Completed or PostProcessingStage.Failed or PostProcessingStage.Cancelled))
            {
                previous.Stage = PostProcessingStage.Cancelled;
                previous.CompletedAt = time.GetUtcNow();
                previous.Error = "explicit post refresh superseded this generation";
                previous.ClaimEnvelopeId = null;
            }
            post.ProcessingRevision++;
        }
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        var id = await QueueAsync(context, outbox, post, operation, search, refresh, ct);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return id;
    }

    private async Task<Guid> QueueAsync(JobFinderDbContext context, IDbContextOutbox outbox, RawPost post, JobFinderLlm operation,
        SearchPolicySnapshot search, bool refresh, CancellationToken ct)
    {
        var input = new PostProcessingInput { Source = PostWitness.Capture(post), Refresh = refresh, Search = search.Value, SearchVersion = search.Version, Operation = operation.CaptureOperation() };
        if (refresh)
        {
            var vacancy = await context.Vacancies.AsNoTracking().SingleAsync(row => row.Id == post.VacancyId, ct);
            input.VacancyTitle = vacancy.Title;
            input.CompanyName = vacancy.CompanyName;
            input.PreviousContacts = [.. vacancy.Contacts];
        }
        input.Recipe = PostProcessingSteps.Recipe(operation, input);
        var json = JsonSerializer.Serialize(input, PostProcessingJsonContext.Default.PostProcessingInput);
        var request = new PostProcessingRequest
        {
            Id = input.Operation.OperationId, PostId = post.Id, Revision = post.ProcessingRevision, Stage = PostProcessingStage.Reading,
            InputJson = json, InputHash = CanonicalJson.Hash(json), CreatedAt = time.GetUtcNow(),
        };
        ReadInput(request);
        context.PostProcessingRequests.Add(request);
        post.ProcessingRequestId = request.Id;
        await outbox.PublishAsync(new ProcessPost { RequestId = request.Id, Stage = request.Stage });
        return request.Id;
    }

    internal static PostProcessingInput ReadInput(PostProcessingRequest request)
    {
        if (CanonicalJson.Hash(request.InputJson) != request.InputHash) throw new UnsupportedPostProcessingException();
        var input = JsonSerializer.Deserialize(request.InputJson, PostProcessingJsonContext.Default.PostProcessingInput) ?? throw new UnsupportedPostProcessingException();
        if (input.Version != 1 || input.Source is null || input.Source.Id != request.PostId || input.Source.ProcessingRevision != request.Revision
            || request.PostId <= 0 || input.Source.Text is null || input.Source.ChatRef is null || input.Source.ExternalId is null || input.Source.Links is null
            || !Enum.IsDefined(input.Source.Source) || input.Source.Source is not (SourceKind.Telegram or SourceKind.Threads)
            || input.Operation is null || input.Operation.OperationId != request.Id || input.Recipe is null || input.Recipe.OperationId != request.Id
            || input.Search is null || input.SearchVersion <= 0
            || input.Refresh && (input.Source.VacancyId is null || string.IsNullOrWhiteSpace(input.VacancyTitle) || input.PreviousContacts is null))
            throw new UnsupportedPostProcessingException();
        SearchPolicyCompiler.Validate(input.Search);
        return input;
    }

    public async Task<PostProcessingResult> WaitAsync(Guid id, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await using var context = await db.CreateDbContextAsync(ct);
            var request = await context.PostProcessingRequests.AsNoTracking().SingleAsync(row => row.Id == id, ct);
            ReadInput(request);
            if (request.Stage == PostProcessingStage.Completed)
            {
                if (request.ResultJson is null || CanonicalJson.Hash(request.ResultJson) != request.ResultHash) throw new UnsupportedPostProcessingException();
                var result = JsonSerializer.Deserialize(request.ResultJson, PostProcessingJsonContext.Default.PostProcessingResult) ?? throw new UnsupportedPostProcessingException();
                if (result.NewContacts is null || result.Failures is null || result.Before < 0 || result.After < 0 || result.Pages < 0
                    || result.VacancyId is <= 0 || result.Created && result.VacancyId is null) throw new UnsupportedPostProcessingException();
                return result;
            }
            if (request.Stage == PostProcessingStage.Cancelled) throw new PostProcessingSupersededException();
            if (request.Stage == PostProcessingStage.Failed || request.RetryAt > time.GetUtcNow()) throw new LlmUnavailableException("post processing is saved: " + request.Error);
            await Task.Delay(TimeSpan.FromMilliseconds(250), time, ct);
        }
    }
}
