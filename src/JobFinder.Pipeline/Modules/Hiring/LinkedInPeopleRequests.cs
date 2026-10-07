using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Integrations.LinkedIn;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed class LinkedInPeopleRequests(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes, JobFinderLlm llm, TimeProvider time)
{
    public async Task<Guid?> ReusableAsync(string company, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(company)) throw new ArgumentException("company required", nameof(company));
        var cutoff = time.GetUtcNow().AddDays(-1);
        await using var context = await db.CreateDbContextAsync(ct);
        var root = await context.LinkedInPeopleRequests.FromSqlInterpolated($"SELECT * FROM linked_in_people_requests WHERE lower(input_json->>'company') = lower({company.Trim()})")
            .AsNoTracking().Where(row => row.Stage == LinkedInPeopleStage.Reading || row.Stage == LinkedInPeopleStage.Completed && row.CreatedAt > cutoff)
            .OrderByDescending(row => row.CreatedAt).FirstOrDefaultAsync(ct);
        if (root is null) return null;
        var input = ReadInput(root);
        var operation = await llm.ForOperationAsync(ct);
        if (LinkedInPeopleSteps.Recipe(operation, input).Hash != input.Recipe.Hash) return null;
        if (root.Stage == LinkedInPeopleStage.Completed) ReadResult(root);
        return root.Id;
    }

    public async Task<Guid> RequestAsync(string company, LinkedInSearchPage page, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(company) || page.Cards is null || page.Parsed is null) throw new ArgumentException("invalid LinkedIn people input");
        var input = new LinkedInPeopleInput
        {
            Company = company.Trim(), Cards = page.Cards.Select(card => new LinkedInPeopleCard { ProfileUrl = card.ProfileUrl, Text = card.Text }).ToList(),
            Parsed = page.Parsed.Select(person => new LinkedInPeopleParsed { Name = person.Name, Headline = person.Headline, ProfileUrl = person.ProfileUrl, Details = person.Details }).ToList(),
        };
        var operation = await llm.ForOperationAsync(ct);
        input.Operation = operation.CaptureOperation();
        input.Recipe = LinkedInPeopleSteps.Recipe(operation, input);
        var json = JsonSerializer.Serialize(input, LinkedInPeopleJsonContext.Default.LinkedInPeopleInput);
        var key = Key(input);
        var request = new LinkedInPeopleRequest { Id = Guid.NewGuid(), BusinessKey = key, Stage = LinkedInPeopleStage.Reading,
            InputJson = json, InputHash = CanonicalJson.Hash(json), CreatedAt = time.GetUtcNow() };
        ReadInput(request);
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await LockAsync(context, key, ct);
        var old = await context.LinkedInPeopleRequests.AsNoTracking().SingleOrDefaultAsync(row => row.BusinessKey == key, ct);
        if (old is not null) return old.Id;
        context.LinkedInPeopleRequests.Add(request);
        await outbox.PublishAsync(new ReadLinkedInPeople { RequestId = request.Id });
        await outbox.SaveChangesAndFlushMessagesAsync(ct);
        return request.Id;
    }

    public async Task<(LinkedInPeopleInput Input, LinkedInPeopleResult Result)> WaitAsync(Guid id, CancellationToken ct)
    {
        while (true)
        {
            await using var context = await db.CreateDbContextAsync(ct);
            var root = await context.LinkedInPeopleRequests.AsNoTracking().SingleAsync(row => row.Id == id, ct);
            if (root.Stage == LinkedInPeopleStage.Completed)
            {
                var input = ReadInput(root);
                if (LinkedInPeopleSteps.Recipe(llm.RestoreOperation(input.Operation), input).Hash != input.Recipe.Hash) throw new UnsupportedLinkedInPeopleException();
                return (input, ReadResult(root));
            }
            if (root.Stage != LinkedInPeopleStage.Reading) throw new UnsupportedLinkedInPeopleException();
            await Task.Delay(TimeSpan.FromMilliseconds(250), time, ct);
        }
    }

    internal static LinkedInPeopleInput ReadInput(LinkedInPeopleRequest root)
    {
        var input = JsonSerializer.Deserialize(root.InputJson, LinkedInPeopleJsonContext.Default.LinkedInPeopleInput) ?? throw new UnsupportedLinkedInPeopleException();
        if (CanonicalJson.Hash(root.InputJson) != root.InputHash || input.Version != 1 || string.IsNullOrWhiteSpace(input.Company)
            || input.Cards is null || input.Parsed is null || input.Cards.Count > 200 || input.Parsed.Count > 200
            || input.Cards.Any(card => card is null || card.Text is null || !ProfileUrl(card.ProfileUrl))
            || input.Parsed.Any(person => person is null || person.Name is null || person.Headline is null || person.Details is null || !ProfileUrl(person.ProfileUrl))
            || input.Operation is null || input.Operation.SchemaVersion != 1 || input.Operation.OperationId == Guid.Empty || input.Operation.Policy is null
            || input.Recipe is null || input.Recipe.Version != 1 || input.Recipe.Tasks is not { Count: 1 } || input.Recipe.Inputs is null || input.Recipe.Rules is null
            || input.Recipe.OperationId != input.Operation.OperationId || Key(input) != root.BusinessKey) throw new UnsupportedLinkedInPeopleException();
        return input;
    }

    internal static LinkedInPeopleResult ReadResult(LinkedInPeopleRequest root)
    {
        if (root.ResultJson is null || CanonicalJson.Hash(root.ResultJson) != root.ResultHash) throw new UnsupportedLinkedInPeopleException();
        var result = JsonSerializer.Deserialize(root.ResultJson, LinkedInPeopleJsonContext.Default.LinkedInPeopleResult) ?? throw new UnsupportedLinkedInPeopleException();
        if (result.Version != 1 || result.Reading is { People: null } || result.Reading?.People.Any(person => person is null) == true || result.Reading?.Validate() is not null
            || ReadInput(root).Cards.Count > 0 && result.Reading is null) throw new UnsupportedLinkedInPeopleException();
        return result;
    }

    private static bool ProfileUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https"
        && (uri.Host == "linkedin.com" || uri.Host.EndsWith(".linkedin.com", StringComparison.OrdinalIgnoreCase)) && uri.AbsolutePath.StartsWith("/in/", StringComparison.Ordinal);

    private static string Key(LinkedInPeopleInput input) => Fingerprint.Of($"linkedin-people-v1\n{input.Recipe.Hash}\n{CanonicalJson.Hash(JsonSerializer.Serialize(input.Page()))}");

    internal static Task LockAsync(JobFinderDbContext context, string key, CancellationToken ct) =>
        context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"hiring.linkedin.people." + key},0))", ct);
}
