using System.Globalization;
using JobFinder.Core.Artifacts;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Integrations.HeadHunter;
using JobFinder.Integrations.Telegram;
using JobFinder.Llm;
using JobFinder.Llm.Client;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Ingest;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Discovery;

public interface IPostProcessingSteps
{
    void EnsureCompatible(PostProcessingInput input);
    Task<PostReading> ReadAsync(PostProcessingInput input, CancellationToken ct);
    Task<PostExtractionCheckpoint> ExtractAsync(PostProcessingInput input, PostReading reading, CancellationToken ct);
    Task<PostVerification> VerifyAsync(PostProcessingInput input, PostReading reading, PostExtractionCheckpoint extraction, CancellationToken ct);
}

public sealed class PostProcessingSteps(JobFinderLlm llm, PostEnricher enricher, TelegramContactVerifier verifier,
    TelegramUserClient telegram, HhClient hh, IDbContextFactory<JobFinderDbContext> db, TimeProvider time, ILogger<PostProcessingSteps> log) : IPostProcessingSteps
{
    public static ArtifactRecipe Recipe(JobFinderLlm operation, PostProcessingInput input) => operation.Recipe(
        [(JobFinder.Llm.Tasks.Discovery.ExtractPostTask.Definition, LlmPurpose.Extract)], new Dictionary<string, string?> { ["source"] = PostWitness.Hash(input.Source) },
        new Dictionary<string, string> { ["extraction"] = "post-processing-v1", ["refresh"] = input.Refresh.ToString() });

    private JobFinderLlm Operation(PostProcessingInput input)
    {
        var operation = llm.RestoreOperation(input.Operation);
        if (input.Recipe.PolicyRevision != input.Operation.PolicyRevision || Recipe(operation, input).Hash != input.Recipe.Hash)
            throw new PostProcessingSupersededException();
        return operation;
    }

    public void EnsureCompatible(PostProcessingInput input) => Operation(input);

    public async Task<PostReading> ReadAsync(PostProcessingInput input, CancellationToken ct)
    {
        var operation = Operation(input);
        var post = PostWitness.Capture(input.Source);
        if (input.Refresh) await RefetchAsync(post, ct);
        var links = PostEnricher.LinksOf(post);
        var filter = new SearchPolicySnapshot(input.Search, input.SearchVersion).Prefilter();
        if (!input.Refresh && PostEnricher.HeadHunterLink(post, links) is var (host, hhId))
        {
            var reading = new PostReading { Post = post, Links = links, HhHost = host, HhId = hhId };
            if (!filter.HasTechMarker(post.Text)) return reading with { TerminalStatus = RawPostStatus.Skipped, Reason = "ссылка на hh, но нет .NET/C#" };
            await using var context = await db.CreateDbContextAsync(ct);
            var externalId = hhId.ToString(CultureInfo.InvariantCulture);
            reading.ExistingHhVacancyId = await context.VacancySources.Where(row => row.Source == SourceKind.HeadHunter && row.ExternalId == externalId)
                .Select(row => (long?)row.VacancyId).FirstOrDefaultAsync(ct);
            if (reading.ExistingHhVacancyId is null)
            {
                var detail = await hh.GetVacancyAsync(host, hhId, ct);
                reading.HhDraft = VacancyIngestor.HhDraft(detail, null, false);
                reading.HhSource = VacancyIngestor.HhSource(detail, null, time.GetUtcNow());
                reading.HhCompanyId = detail.CompanyId;
            }
            return reading;
        }
        var needsPages = !filter.LooksLikeDotnetVacancy(post.Text, out var reason);
        if (!input.Refresh && needsPages && !enricher.WorthReading(post, links, filter))
            return new() { Post = post, Links = links, TerminalStatus = RawPostStatus.Skipped, Reason = reason };
        if (!input.Refresh && !await operation.MayExtractAsync(ct)) throw new LlmUnavailableException("no extraction model is available now");
        var enriched = await enricher.EnrichAsync(post, links, ct);
        var result = PostReading.Capture(post, enriched);
        if (!input.Refresh && needsPages && !filter.LooksLikeDotnetVacancy(enriched.Description, out var withPages))
        {
            result.TerminalStatus = RawPostStatus.Skipped;
            result.Reason = withPages + " (с " + enriched.Pages.Count + " стр. по ссылкам)";
        }
        return result;
    }

    public async Task<PostExtractionCheckpoint> ExtractAsync(PostProcessingInput input, PostReading reading, CancellationToken ct)
    {
        try
        {
            return new() { Extraction = (await Operation(input).ExtractPostAsync(reading.Post.Text, reading.Post.ChatTitle ?? reading.Post.ChatRef,
                ct, reading.Links, reading.ToEnriched().ModelPages)).Value };
        }
        catch (LlmUnavailableException) when (input.Refresh)
        {
            return new() { Complete = false, Extraction = new() { IsVacancy = true, IsDotnet = true, Title = input.VacancyTitle, Company = input.CompanyName } };
        }
    }

    public async Task<PostVerification> VerifyAsync(PostProcessingInput input, PostReading reading, PostExtractionCheckpoint extraction, CancellationToken ct)
    {
        Operation(input);
        var facts = enricher.Resolve(reading.Post, extraction.Extraction, reading.ToEnriched());
        var result = new PostVerification { Contacts = await verifier.VerifyAsync(facts.Contacts, ct), Url = facts.Url };
        if (input.Refresh)
        {
            await using var context = await db.CreateDbContextAsync(ct);
            var contacts = await context.Vacancies.Where(row => row.Id == input.Source.VacancyId).Select(row => row.Contacts).SingleAsync(ct);
            result.StoredContactsHash = PostWitness.ContactHash(contacts);
            result.StoredContacts = await verifier.VerifyAsync(contacts, ct);
        }
        return result;
    }

    private async Task RefetchAsync(RawPost post, CancellationToken ct)
    {
        if (!telegram.IsReady || !long.TryParse(post.ChatRef, CultureInfo.InvariantCulture, out var peerId)
            || !int.TryParse(post.ExternalId, CultureInfo.InvariantCulture, out var messageId)) return;
        await using var context = await db.CreateDbContextAsync(ct);
        var channel = await context.TelegramChannels.AsNoTracking().FirstOrDefaultAsync(row => row.PeerId == peerId && row.AccessHash != null, ct);
        if (channel is null) return;
        try
        {
            if (await telegram.GetPostAsync(peerId, channel.AccessHash!.Value, channel.Title ?? post.ChatRef, channel.Username, messageId) is { } fresh)
                post.Links = [.. fresh.Links];
        }
        catch (Exception error) when (error is not OperationCanceledException) { log.Warn($"[post-processing] post {post.Id}: Telegram read unavailable ({error.Message})"); }
    }
}
