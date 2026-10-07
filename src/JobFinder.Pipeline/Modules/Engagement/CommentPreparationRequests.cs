using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Client;
using JobFinder.Llm.Prompts;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Engagement;

public sealed class CommentPreparationRequests(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes,
    JobFinderLlm llm, PromptCatalog prompts, TimeProvider time)
{
    internal static Task LockAsync(JobFinderDbContext context, string key, CancellationToken ct) =>
        context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"jobfinder.engagement.comment." + key}, 0))", ct);

    public async Task<Guid> RequestAsync(CandidateProfile profile, string author, string text, CancellationToken ct, bool force = false)
    {
        var input = await CaptureAsync(profile, author, text, ct);
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        var id = await QueueAsync(context, outbox, input, ct, force);
        await outbox.SaveChangesAndFlushMessagesAsync(ct);
        return id;
    }

    public async Task<CommentPreparationInput> CaptureAsync(CandidateProfile profile, string author, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(author) || !CommentPlanner.IsCandidate(text)) throw new ArgumentException("invalid comment source");
        var operation = await llm.ForOperationAsync(ct);
        var input = new CommentPreparationInput
        {
            Profile = profile, Author = author, PostText = text, Operation = operation.CaptureOperation(),
            ExampleShingles = CommentPlanner.ExamplesOf(prompts.Render("threads_comment", new Dictionary<string, string> { ["profile"] = "" }))
                .Order(StringComparer.Ordinal).ToList(),
        };
        input.Recipe = CommentPreparationSteps.Recipe(operation, input);
        return JsonSerializer.Deserialize(JsonSerializer.Serialize(input, CommentPreparationJsonContext.Default.CommentPreparationInput),
            CommentPreparationJsonContext.Default.CommentPreparationInput)!;
    }

    public async Task<Guid> QueueAsync(JobFinderDbContext context, IDbContextOutbox outbox, CommentPreparationInput input, CancellationToken ct, bool force = false)
    {
        if (context.Database.CurrentTransaction is null) throw new InvalidOperationException("comment preparation handoff requires the caller transaction");
        var json = JsonSerializer.Serialize(input, CommentPreparationJsonContext.Default.CommentPreparationInput);
        var key = input.Recipe.Hash;
        await LockAsync(context, key, ct);
        var active = await context.CommentPreparationRequests.SingleOrDefaultAsync(row => row.BusinessKey == key
            && (row.Stage == CommentPreparationStage.Drafting || row.Stage == CommentPreparationStage.Checking), ct);
        if (active is not null && !force) return active.Id;
        if (active is not null)
        {
            active.Stage = CommentPreparationStage.Cancelled;
            active.CompletedAt = time.GetUtcNow();
            active.Error = "explicit regeneration superseded this request";
            active.ClaimEnvelopeId = null;
            await context.SaveChangesAsync(ct);
            await PublishDependentsAsync(context, outbox, active.Id, ct);
        }
        if (!force)
        {
            var completed = await context.CommentPreparationRequests.Where(row => row.BusinessKey == key && row.Stage == CommentPreparationStage.Completed)
                .OrderByDescending(row => row.CompletedAt).FirstOrDefaultAsync(ct);
            if (completed is not null) return completed.Id;
        }
        var request = new CommentPreparationRequest
        {
            Id = input.Operation.OperationId, BusinessKey = key, InputHash = CanonicalJson.Hash(json), InputJson = json,
            Stage = CommentPreparationStage.Drafting, CreatedAt = time.GetUtcNow(),
        };
        context.CommentPreparationRequests.Add(request);
        await outbox.PublishAsync(new DraftComment { RequestId = request.Id });
        return request.Id;
    }

    internal static async Task PublishDependentsAsync(JobFinderDbContext context, IDbContextOutbox outbox, Guid preparationId, CancellationToken ct)
    {
        var ids = await context.CommentSourceRequests.Where(row => row.PreparationRequestId == preparationId
            && row.Stage == CommentSourceStage.Waiting).Select(row => row.Id).ToListAsync(ct);
        foreach (var id in ids) await outbox.PublishAsync(new PrepareCommentSource { RequestId = id });
    }

    public async Task<CommentPreparationResult?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var request = await context.CommentPreparationRequests.AsNoTracking().SingleAsync(row => row.Id == id, ct);
        if (request.Stage == CommentPreparationStage.Cancelled) throw new CommentPreparationSupersededException();
        if (request.Stage == CommentPreparationStage.Failed) throw new LlmUnavailableException(request.Error ?? "comment preparation failed");
        if (request.RetryAt > time.GetUtcNow()) throw new LlmUnavailableException("comment preparation is saved and scheduled for retry: " + request.Error);
        if (request.Stage != CommentPreparationStage.Completed) return null;
        if (CanonicalJson.Hash(request.InputJson) != request.InputHash) throw new UnsupportedCommentPreparationException();
        var input = JsonSerializer.Deserialize(request.InputJson, CommentPreparationJsonContext.Default.CommentPreparationInput)
            ?? throw new JsonException("missing comment input");
        var result = JsonSerializer.Deserialize(request.ResultJson ?? throw new JsonException("missing comment result"), CommentPreparationJsonContext.Default.CommentPreparationResult)
            ?? throw new JsonException("missing comment result");
        if (result.Plan.WorthCommenting)
        {
            var (good, remarks) = CommentPlanner.Review(result.Plan, input.ExampleShingles.ToHashSet(StringComparer.Ordinal));
            if (good.Count != result.Plan.Variants.Count || good.Count == 0 || remarks.Count != 0) throw new JsonException("saved comment result failed style validation");
        }
        return result;
    }

    public async Task<CommentPreparationResult> WaitAsync(Guid id, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (await GetAsync(id, ct) is { } result) return result;
            await Task.Delay(TimeSpan.FromMilliseconds(250), time, ct);
        }
    }
}
