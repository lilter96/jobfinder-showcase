using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using JobFinder.Core.Contacts;
using JobFinder.Core.Domain;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Integrations.HeadHunter;
using JobFinder.Integrations.Http;
using JobFinder.Integrations.Web;
using JobFinder.Llm.Client;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Ingest;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Discovery;

public sealed class PostProcessingWorkflow(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes,
    IPostProcessingSteps steps, VacancyIngestor ingestor, ContactRegistry contacts, LinkPolicy links, ServerOwnership ownership,
    Notifier notifier, TimeProvider time)
{
    public async Task RunAsync(ProcessPost command, Guid envelopeId, CancellationToken ct)
    {
        if (command.RequestId == Guid.Empty || envelopeId == Guid.Empty) throw new UnsupportedPostProcessingException();
        ownership.EnsureOwned();
        if (command.Version != 1 || command.Stage is not (PostProcessingStage.Reading or PostProcessingStage.Extracting or PostProcessingStage.Verifying or PostProcessingStage.Publishing))
        {
            await RetireAsync(command.RequestId, PostProcessingStage.Failed, "unsupported post command", ct, command.Stage);
            throw new UnsupportedPostProcessingException();
        }
        var request = await ClaimAsync(command, envelopeId, ct);
        if (request is null) return;
        using var span = Telemetry.StartStage(command.Stage.ToString().ToLowerInvariant(), "post-processing").Tag("request.id", request.Id).Tag("post.id", request.PostId);
        try
        {
            var input = PostProcessingRequests.ReadInput(request);
            if (!await ChangeAsync(request, (_, _, current) =>
            {
                current.ClaimEnvelopeId = request.ClaimEnvelopeId;
                return Task.CompletedTask;
            }, ct)) return;
            steps.EnsureCompatible(input);
            ownership.EnsureOwned();
            if (command.Stage == PostProcessingStage.Reading)
            {
                var reading = await steps.ReadAsync(input, ct);
                await ChangeAsync(request, async (_, outbox, current) =>
                {
                    current.ReadingJson = JsonSerializer.Serialize(reading, PostProcessingJsonContext.Default.PostReading);
                    current.ReadingHash = CanonicalJson.Hash(current.ReadingJson);
                    var next = reading.TerminalStatus is not null || !input.Refresh && reading.HhId is not null ? PostProcessingStage.Publishing : PostProcessingStage.Extracting;
                    await AdvanceAsync(current, outbox, next);
                }, ct);
            }
            else if (command.Stage == PostProcessingStage.Extracting)
            {
                var extraction = await steps.ExtractAsync(input, Reading(request, input), ct);
                if (extraction.Extraction.Validate() is not null) throw new JsonException("invalid saved extraction");
                await ChangeAsync(request, async (_, outbox, current) =>
                {
                    current.ExtractionJson = JsonSerializer.Serialize(extraction, PostProcessingJsonContext.Default.PostExtractionCheckpoint);
                    current.ExtractionHash = CanonicalJson.Hash(current.ExtractionJson);
                    var next = !input.Refresh && (!extraction.Extraction.IsVacancy || !extraction.Extraction.IsDotnet) ? PostProcessingStage.Publishing : PostProcessingStage.Verifying;
                    await AdvanceAsync(current, outbox, next);
                }, ct);
            }
            else if (command.Stage == PostProcessingStage.Verifying)
            {
                var verification = await steps.VerifyAsync(input, Reading(request, input), Extracted(request), ct);
                await ChangeAsync(request, async (_, outbox, current) =>
                {
                    current.VerificationJson = JsonSerializer.Serialize(verification, PostProcessingJsonContext.Default.PostVerification);
                    current.VerificationHash = CanonicalJson.Hash(current.VerificationJson);
                    await AdvanceAsync(current, outbox, PostProcessingStage.Publishing);
                }, ct);
            }
            else await PublishAsync(request, input, ct);
        }
        catch (LlmUnavailableException error) { await DeferAsync(request, error.Message, time.GetUtcNow().AddMinutes(error.BudgetExceeded || error.Unauthorized ? 30 : 2), false, ct); }
        catch (SourceBlockedException error) { await DeferAsync(request, error.Message, error.Until ?? time.GetUtcNow().AddMinutes(10), false, ct); }
        catch (PostContactsChangedException)
        {
            try
            {
                await ChangeAsync(request, async (_, outbox, current) =>
                {
                    current.VerificationJson = null;
                    current.VerificationHash = null;
                    await AdvanceAsync(current, outbox, PostProcessingStage.Verifying);
                }, ct);
            }
            catch (PostProcessingSupersededException) { await RetireAsync(request.Id, PostProcessingStage.Cancelled, "source changed during contact verification", ct, request.Stage, request.ClaimEnvelopeId); }
        }
        catch (PostProcessingSupersededException) { await RetireAsync(request.Id, PostProcessingStage.Cancelled, "source or recipe changed", ct, request.Stage, request.ClaimEnvelopeId); }
        catch (Exception error) when (error is UnsupportedPostProcessingException or JsonException or ArgumentException)
        {
            await RetireAsync(request.Id, PostProcessingStage.Failed, "unsupported post checkpoint", ct, request.Stage, request.ClaimEnvelopeId);
            throw new UnsupportedPostProcessingException();
        }
        catch (Exception error) when (error is not OperationCanceledException) { await DeferAsync(request, error.Message, time.GetUtcNow().AddMinutes(1), true, ct); }
    }

    private async Task<PostProcessingRequest?> ClaimAsync(ProcessPost command, Guid envelopeId, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var request = await context.PostProcessingRequests.SingleOrDefaultAsync(row => row.Id == command.RequestId, ct);
        if (request is null) return null;
        await LockPostAsync(context, request.PostId, ct);
        await context.Entry(request).ReloadAsync(ct);
        if (request.Stage != command.Stage || request.RetryAt > time.GetUtcNow() || request.ClaimEnvelopeId is { } claim && claim != envelopeId) return null;
        ownership.EnsureOwned();
        request.ClaimEnvelopeId = envelopeId;
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return request;
    }

    private async Task<bool> ChangeAsync(PostProcessingRequest before, Func<JobFinderDbContext, IDbContextOutbox, PostProcessingRequest, Task> change, CancellationToken ct)
    {
        ownership.EnsureOwned();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        await LockPostAsync(context, before.PostId, ct);
        var current = await context.PostProcessingRequests.SingleAsync(row => row.Id == before.Id, ct);
        if (current.Stage != before.Stage || current.ClaimEnvelopeId != before.ClaimEnvelopeId || current.InputHash != before.InputHash) return false;
        var input = PostProcessingRequests.ReadInput(current);
        var post = await context.RawPosts.SingleOrDefaultAsync(row => row.Id == before.PostId, ct);
        if (post is null || !PostWitness.Matches(post, current, input)) throw new PostProcessingSupersededException();
        current.ClaimEnvelopeId = null;
        current.RetryAt = null;
        current.Error = null;
        ownership.EnsureOwned();
        await change(context, outbox, current);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return true;
    }

    private async Task PublishAsync(PostProcessingRequest request, PostProcessingInput input, CancellationToken ct)
    {
        var reading = Reading(request, input);
        var result = new PostProcessingResult { Pages = reading.Pages.Count, Before = input.PreviousContacts.Count, Failures = [.. reading.Failures] };
        var terminal = reading.TerminalStatus;
        if (!input.Refresh && terminal is null && reading.HhId is null)
        {
            var extraction = Extracted(request).Extraction;
            if (!extraction.IsVacancy || !extraction.IsDotnet) terminal = RawPostStatus.NotVacancy;
        }
        if (terminal is { } status)
        {
            await ChangeAsync(request, async (context, _, current) =>
            {
                var post = await context.RawPosts.SingleAsync(row => row.Id == current.PostId, ct);
                post.Status = status;
                post.Error = reading.Reason ?? (status == RawPostStatus.Skipped ? "пост отсеян до извлечения"
                    : !Extracted(current).Extraction.IsVacancy ? "LLM: не вакансия" : "LLM: не .NET");
                Finish(current, result);
            }, ct);
            return;
        }
        if (!input.Refresh && reading.HhId is not null)
        {
            if (reading.ExistingHhVacancyId is { } existing)
            {
                await using var context = await db.CreateDbContextAsync(ct);
                var externalId = reading.HhId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!await context.VacancySources.AnyAsync(source => source.VacancyId == existing && source.Source == SourceKind.HeadHunter && source.ExternalId == externalId, ct))
                    throw new PostProcessingSupersededException();
                await ingestor.AttachExistingPostAsync(reading.Post, existing, CompleteInitialAsync, ct);
            }
            else await ingestor.IngestHhPostAsync(reading.Post, reading.HhDraft ?? throw new JsonException("missing hh draft"),
                reading.HhSource ?? throw new JsonException("missing hh source"), reading.HhCompanyId, CompleteInitialAsync, ct);
            return;
        }
        var extracted = Extracted(request);
        var verified = Read(request.VerificationJson, request.VerificationHash, PostProcessingJsonContext.Default.PostVerification);
        if (verified.Contacts is null || verified.StoredContacts is null) throw new JsonException("missing verified contacts");
        var facts = new PostFacts(verified.Contacts, verified.Url);
        if (!input.Refresh)
        {
            await ingestor.IngestPostAsync(reading.Post, extracted.Extraction, reading.ToEnriched(), facts, ct, CompleteInitialAsync);
            return;
        }
        var draft = VacancyIngestor.PostDraft(reading.Post, extracted.Extraction, reading.ToEnriched(), facts);
        var ownUrls = reading.Links.Select(link => link.Url).Concat(reading.Pages.Select(page => page.Url)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = false;
        await ingestor.RefreshAsync(input.Source.VacancyId!.Value, draft, url => ownUrls.Contains(url) || NotAVacancyPage(url), ct, extracted.Complete,
            async (context, vacancy, token) =>
            {
                if (PostWitness.ContactHash(context.Entry(vacancy).OriginalValues.GetValue<List<VacancyContact>>(nameof(Vacancy.Contacts))) != verified.StoredContactsHash)
                    throw new PostContactsChangedException();
                var allowed = verified.Contacts.Concat(verified.StoredContacts).Select(ContactExtractor.Key).ToHashSet();
                vacancy.Contacts = vacancy.Contacts.Where(contact => allowed.Contains(ContactExtractor.Key(contact))).ToList();
                var current = await ValidatePublicationAsync(context, request, input, token);
                result.VacancyId = vacancy.Id;
                result.After = vacancy.Contacts.Count;
                var previous = input.PreviousContacts.Select(ContactExtractor.Key).ToHashSet();
                result.NewContacts = vacancy.Contacts.Where(contact => !previous.Contains(ContactExtractor.Key(contact))).Select(contact => contact.ToString()).ToList();
                await contacts.FromPostAsync(context, vacancy.CompanyName ?? draft.CompanyName, vacancy.CompanyId, draft.Contacts, reading.Post.Url ?? reading.Post.ChatRef, token);
                var post = await context.RawPosts.SingleAsync(row => row.Id == request.PostId, token);
                post.Links = [.. reading.Post.Links];
                Finish(current, result);
                found = true;
            });
        if (!found) throw new PostProcessingSupersededException();

        async Task CompleteInitialAsync(JobFinderDbContext context, IngestResult ingest, CancellationToken token)
        {
            var current = await ValidatePublicationAsync(context, request, input, token);
            var post = await context.RawPosts.SingleAsync(row => row.Id == request.PostId, token);
            post.Status = RawPostStatus.Vacancy;
            post.VacancyId = ingest.VacancyId;
            post.Error = null;
            result.VacancyId = ingest.VacancyId;
            result.Created = ingest.Created;
            if (reading.HhId is null)
            {
                var verification = Read(request.VerificationJson, request.VerificationHash, PostProcessingJsonContext.Default.PostVerification);
                result.After = verification.Contacts.Count;
                var company = Extracted(request).Extraction.Company ?? reading.Posting?.Company;
                if (company is not null) await contacts.FromPostAsync(context, company, null, verification.Contacts, post.Url ?? post.ChatRef + "/" + post.ExternalId, token);
            }
            if (ingest.Created && post.Source == SourceKind.Telegram && long.TryParse(post.ChatRef, out var peerId))
                await context.TelegramChannels.Where(row => row.PeerId == peerId).ExecuteUpdateAsync(update => update.SetProperty(row => row.VacanciesFound, row => row.VacanciesFound + 1), token);
            Finish(current, result);
        }
    }

    private async Task<PostProcessingRequest> ValidatePublicationAsync(JobFinderDbContext context, PostProcessingRequest before, PostProcessingInput input, CancellationToken ct)
    {
        ownership.EnsureOwned();
        await LockPostAsync(context, before.PostId, ct);
        var current = await context.PostProcessingRequests.SingleAsync(row => row.Id == before.Id, ct);
        var post = await context.RawPosts.SingleOrDefaultAsync(row => row.Id == before.PostId, ct);
        if (current.Stage != PostProcessingStage.Publishing || current.ClaimEnvelopeId != before.ClaimEnvelopeId || current.InputHash != before.InputHash
            || current.ReadingHash != before.ReadingHash || current.ExtractionHash != before.ExtractionHash || current.VerificationHash != before.VerificationHash
            || post is null || !PostWitness.Matches(post, current, input)) throw new PostProcessingSupersededException();
        PostProcessingRequests.ReadInput(current);
        ownership.EnsureOwned();
        return current;
    }

    private async Task DeferAsync(PostProcessingRequest request, string error, DateTimeOffset at, bool countFailure, CancellationToken ct)
    {
        try
        {
            await ChangeAsync(request, async (context, outbox, current) =>
            {
                current.Error = error;
                if (countFailure)
                {
                    current.Failures++;
                    var post = await context.RawPosts.SingleAsync(row => row.Id == current.PostId, ct);
                    post.Attempts = current.Failures;
                    post.Error = error;
                    if (current.Failures >= 3)
                    {
                        current.Stage = PostProcessingStage.Failed;
                        current.CompletedAt = time.GetUtcNow();
                        if (!PostProcessingRequests.ReadInput(current).Refresh) post.Status = RawPostStatus.Failed;
                        notifier.Add(context, NotificationKind.Alert, "⚠️ Пост #" + current.PostId + " не обработан: " + TextUtil.EscapeHtml(error));
                        return;
                    }
                }
                current.RetryAt = at;
                await outbox.PublishAsync(new ProcessPost { RequestId = current.Id, Stage = current.Stage }, new DeliveryOptions { ScheduledTime = at });
            }, ct);
        }
        catch (PostProcessingSupersededException) { await RetireAsync(request.Id, PostProcessingStage.Cancelled, "source changed during a retry boundary", ct, request.Stage, request.ClaimEnvelopeId); }
    }

    private async Task RetireAsync(Guid id, PostProcessingStage stage, string error, CancellationToken ct, PostProcessingStage expectedStage, Guid? expectedClaim = null)
    {
        ownership.EnsureOwned();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var current = await context.PostProcessingRequests.SingleOrDefaultAsync(row => row.Id == id, ct);
        if (current is null) return;
        await LockPostAsync(context, current.PostId, ct);
        await context.Entry(current).ReloadAsync(ct);
        if (current.Stage != expectedStage || expectedClaim is not null && current.ClaimEnvelopeId != expectedClaim) return;
        if (current.Stage is PostProcessingStage.Completed or PostProcessingStage.Failed or PostProcessingStage.Cancelled) return;
        current.Stage = stage;
        current.Error = error;
        current.CompletedAt = time.GetUtcNow();
        current.ClaimEnvelopeId = null;
        var post = await context.RawPosts.SingleOrDefaultAsync(row => row.Id == current.PostId, ct);
        if (post is not null && post.ProcessingRequestId == id && post.ProcessingRevision == current.Revision)
        {
            if (stage == PostProcessingStage.Cancelled) { post.ProcessingRequestId = null; post.ProcessingRevision++; }
            else if (post.Status == RawPostStatus.New) post.Status = RawPostStatus.Failed;
        }
        notifier.Add(context, NotificationKind.Alert, "⚠️ Обработка поста #" + current.PostId + " остановлена: " + TextUtil.EscapeHtml(error));
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, ct);
    }

    private void Finish(PostProcessingRequest request, PostProcessingResult result)
    {
        request.ResultJson = JsonSerializer.Serialize(result, PostProcessingJsonContext.Default.PostProcessingResult);
        request.ResultHash = CanonicalJson.Hash(request.ResultJson);
        request.Stage = PostProcessingStage.Completed;
        request.CompletedAt = time.GetUtcNow();
        request.ClaimEnvelopeId = null;
        request.RetryAt = null;
        request.Error = null;
    }

    private static async Task AdvanceAsync(PostProcessingRequest request, IDbContextOutbox outbox, PostProcessingStage next)
    {
        request.Stage = next;
        await outbox.PublishAsync(new ProcessPost { RequestId = request.Id, Stage = next });
    }

    private static T Read<T>(string? json, string? hash, JsonTypeInfo<T> type) => json is null || CanonicalJson.Hash(json) != hash
        ? throw new JsonException("damaged post checkpoint") : JsonSerializer.Deserialize(json, type) ?? throw new JsonException("missing post checkpoint");
    private static PostExtractionCheckpoint Extracted(PostProcessingRequest request)
    {
        var value = Read(request.ExtractionJson, request.ExtractionHash, PostProcessingJsonContext.Default.PostExtractionCheckpoint);
        if (value.Extraction is null || value.Extraction.Validate() is not null) throw new JsonException("invalid extraction checkpoint");
        return value;
    }
    private static PostReading Reading(PostProcessingRequest request, PostProcessingInput input)
    {
        var reading = Read(request.ReadingJson, request.ReadingHash, PostProcessingJsonContext.Default.PostReading);
        if (reading.Post is null || reading.Links is null || reading.Pages is null || reading.Contacts is null || reading.Failures is null || reading.Missing is null)
            throw new JsonException("missing reading checkpoint");
        var witness = PostWitness.Capture(reading.Post);
        if (input.Refresh) witness.Links = [.. input.Source.Links];
        if (PostWitness.Hash(witness) != PostWitness.Hash(input.Source)) throw new JsonException("reading changed the captured source");
        if (reading.TerminalStatus is not null && (input.Refresh || reading.TerminalStatus != RawPostStatus.Skipped)) throw new JsonException("invalid terminal reading");
        if (!input.Refresh && reading.HhId is { } hhId && PostEnricher.HeadHunterLink(input.Source, PostEnricher.LinksOf(input.Source)) != (reading.HhHost, hhId))
            throw new JsonException("hh checkpoint changed the captured target");
        if (!input.Refresh && reading.HhId is { } targetId && reading.ExistingHhVacancyId is null && reading.TerminalStatus is null
            && (reading.HhDraft is null || string.IsNullOrWhiteSpace(reading.HhDraft.Title) || reading.HhDraft.Description is null
                || reading.HhSource is null || reading.HhSource.Source != SourceKind.HeadHunter || reading.HhSource.Host != reading.HhHost
                || reading.HhSource.ExternalId != targetId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                || !HhClient.TryParseVacancyUrl(reading.HhSource.Url, out var sourceHost, out var sourceId) || sourceHost != reading.HhHost || sourceId != targetId
                || reading.HhDraft.Url != reading.HhSource.Url))
            throw new JsonException("invalid structured hh checkpoint");
        return reading;
    }
    private bool NotAVacancyPage(string url) => !Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.AbsolutePath is "/" or ""
        || links.Classify(url).Role is not (LinkRole.Page or LinkRole.HeadHunter);
    private static Task LockPostAsync(JobFinderDbContext context, long id, CancellationToken ct) =>
        context.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM raw_posts WHERE id = {id} FOR UPDATE", ct);
}

[NonTransactional]
public static class PostProcessingHandler
{
    public static Task Handle(ProcessPost command, PostProcessingWorkflow workflow, IMessageContext message, CancellationToken ct) =>
        workflow.RunAsync(command, message.Envelope?.Id ?? throw new UnsupportedPostProcessingException(), ct);
}
