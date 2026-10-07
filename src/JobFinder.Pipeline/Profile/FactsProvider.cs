using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using JobFinder.Core.Profile;
using JobFinder.Core.Storage;
using JobFinder.Pipeline.Modules.Candidate;

namespace JobFinder.Pipeline.Profile;

/// <summary>
/// The answers to screening questions: the document <see cref="Key"/> in the database. Missing → a draft from the profile is stored
/// (nothing confirmed): the system answers employers on its own only with the facts the user has confirmed on the dashboard page «Факты».
/// </summary>
public sealed class FactsProvider(IDocumentStore documents, ILogger<FactsProvider> log, ICandidatePublisher publisher)
{
    public const string Key = "candidate.facts";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = FactsJsonContext.Default,
    };

    private static readonly JsonTypeInfo<CandidateFacts> Write = (JsonTypeInfo<CandidateFacts>)WriteOptions.GetTypeInfo(typeof(CandidateFacts));

    private readonly DocumentCache<CandidateFacts> _facts = new(documents, Key, FactsJsonContext.Default.CandidateFacts, log, Write,
        facts => facts.WithStandardQuestions());

    /// <summary>Facts text as the store keeps it: read the way the old file was (comments, trailing commas), written as plain JSON.</summary>
    public static string Normalize(string json) =>
        JsonSerializer.Serialize(JsonSerializer.Deserialize(json, FactsJsonContext.Default.CandidateFacts) ?? throw new JsonException("пустые факты"), Write);

    /// <summary>The facts, shared with every caller: change a copy (<see cref="SaveAsync"/>), never this instance.</summary>
    public async Task<CandidateFacts> GetAsync(CancellationToken ct)
    {
        if (await _facts.GetAsync(ct) is { } facts) return facts;
        await publisher.EnsureFactsAsync(ct);
        return await _facts.GetAsync(ct) ?? new CandidateFacts().WithStandardQuestions();
    }

    public async Task<VersionedDocument<CandidateFacts>> GetSnapshotAsync(CancellationToken ct)
    {
        var document = await documents.GetAsync(Key, ct);
        if (document is null)
        {
            await publisher.EnsureFactsAsync(ct);
            document = await documents.GetAsync(Key, ct);
        }
        if (document is null) return new VersionedDocument<CandidateFacts>(new CandidateFacts().WithStandardQuestions(), 0);
        var value = JsonSerializer.Deserialize(document.Json, FactsJsonContext.Default.CandidateFacts) ?? throw new JsonException("пустые факты");
        return new VersionedDocument<CandidateFacts>(value.WithStandardQuestions(), document.Version);
    }

    /// <summary>One fact set by the user (a position picked on a preparation page): confirmed, in their words.</summary>
    public async Task UpsertAsync(string key, string question, string answer, CancellationToken ct)
    {
        var initial = await GetSnapshotAsync(ct);
        var original = initial.Value.Get(key) is { } existing ? existing with { } : null;
        for (var attempt = 0; ; attempt++)
        {
            var snapshot = attempt == 0 ? initial : await GetSnapshotAsync(ct);
            if (attempt > 0 && snapshot.Value.Get(key) != original)
                throw new CandidateConflictException(Key, initial.Version);
            var copy = snapshot.Value;
            if (copy.Get(key) is { } fact)
            {
                fact.Answer = answer;
                fact.Confirmed = true;
                fact.Source = "вы";
            }
            else
                copy.Facts.Add(new CandidateFact { Key = key, Question = question, Answer = answer, Confirmed = true, Source = "вы" });
            try
            {
                await SaveAsync(copy, snapshot.Version, "candidate-answer", ct);
                return;
            }
            catch (CandidateConflictException) when (attempt < 15) { }
        }
    }

    public Task<VersionedDocument<CandidateFacts>> SaveAsync(CandidateFacts facts, long expectedVersion, string actor, CancellationToken ct) =>
        publisher.PublishFactsAsync(facts, expectedVersion, actor, ct);
}
