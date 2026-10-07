using System.Text.Json;
using System.Text.Json.Serialization;
using JobFinder.Core.Artifacts;
using JobFinder.Core.Domain;
using JobFinder.Core.Policies;
using JobFinder.Core.Text;
using JobFinder.Integrations.Web;
using JobFinder.Llm.Configuration;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Ingest;

namespace JobFinder.Pipeline.Modules.Discovery;

public sealed record PostProcessingInput
{
    public int Version { get; set; } = 1;
    public RawPost Source { get; set; } = null!;
    public bool Refresh { get; set; }
    public string? VacancyTitle { get; set; }
    public string? CompanyName { get; set; }
    public List<VacancyContact> PreviousContacts { get; set; } = [];
    public SearchPolicy Search { get; set; } = new();
    public long SearchVersion { get; set; }
    public LlmOperationSnapshot Operation { get; set; } = new();
    public ArtifactRecipe Recipe { get; set; } = new();
}

public sealed record PostPosting
{
    public string? Title { get; set; }
    public string? Company { get; set; }
    public string? Url { get; set; }
    public decimal? SalaryFrom { get; set; }
    public decimal? SalaryTo { get; set; }
    public string? Currency { get; set; }
    public bool Remote { get; set; }
    public string? EmploymentType { get; set; }
    public DateTimeOffset? DatePosted { get; set; }
    public string? Description { get; set; }
    public List<VacancyContact> Contacts { get; set; } = [];
    public JobPostingData ToPosting() => new(Title, Company, Url, SalaryFrom, SalaryTo, Currency, Remote, EmploymentType, DatePosted, Description, Contacts);
    public static PostPosting? Capture(JobPostingData? value) => value is null ? null : new()
    {
        Title = value.Title, Company = value.Company, Url = value.Url, SalaryFrom = value.SalaryFrom, SalaryTo = value.SalaryTo,
        Currency = value.Currency, Remote = value.Remote, EmploymentType = value.EmploymentType, DatePosted = value.DatePosted,
        Description = value.Description, Contacts = [.. value.Contacts],
    };
}

public sealed record PostPage
{
    public string Url { get; set; } = "";
    public string? Title { get; set; }
    public string Text { get; set; } = "";
    public List<PostLink> Links { get; set; } = [];
    public PostPosting? Posting { get; set; }
    public int Depth { get; set; }
    public string? Via { get; set; }
    public bool Rendered { get; set; }
    public bool ViaProxy { get; set; }
    public CrawledPage ToPage() => new(new(new Uri(Url, UriKind.Absolute), Title, Text, Links, Posting?.ToPosting()), Depth, Via, Rendered, ViaProxy);
    public static PostPage Capture(CrawledPage value) => new()
    {
        Url = value.Page.Url.AbsoluteUri, Title = value.Page.Title, Text = value.Page.Text, Links = [.. value.Page.Links],
        Posting = PostPosting.Capture(value.Page.JobPosting), Depth = value.Depth, Via = value.Via, Rendered = value.Rendered, ViaProxy = value.ViaProxy,
    };
}

public sealed record PostReading
{
    public RawPost Post { get; set; } = null!;
    public List<PostLink> Links { get; set; } = [];
    public List<PostPage> Pages { get; set; } = [];
    public List<VacancyContact> Contacts { get; set; } = [];
    public string Description { get; set; } = "";
    public string? DetailUrl { get; set; }
    public PostPosting? Posting { get; set; }
    public string? HhHost { get; set; }
    public long? HhId { get; set; }
    public long? ExistingHhVacancyId { get; set; }
    public Vacancy? HhDraft { get; set; }
    public VacancySource? HhSource { get; set; }
    public long? HhCompanyId { get; set; }
    public List<string> Failures { get; set; } = [];
    public List<string> Missing { get; set; } = [];
    public RawPostStatus? TerminalStatus { get; set; }
    public string? Reason { get; set; }
    public EnrichedPost ToEnriched() => new(Links, Pages.Select(page => page.ToPage()).ToList(), Contacts, Description, DetailUrl,
        Posting?.ToPosting(), HhHost is not null && HhId is { } id ? (HhHost, id) : null, Failures, Missing.ToHashSet(StringComparer.Ordinal));
    public static PostReading Capture(RawPost post, EnrichedPost value) => new()
    {
        Post = post, Links = [.. value.Links], Pages = value.Pages.Select(PostPage.Capture).ToList(), Contacts = [.. value.Contacts],
        Description = value.Description, DetailUrl = value.DetailUrl, Posting = PostPosting.Capture(value.Posting),
        HhHost = value.HeadHunter?.Host, HhId = value.HeadHunter?.Id, Failures = [.. value.Failures], Missing = value.Missing?.Order().ToList() ?? [],
    };
}

public sealed record PostExtractionCheckpoint
{
    public PostExtraction Extraction { get; set; } = new();
    public bool Complete { get; set; } = true;
}

public sealed record PostVerification
{
    public List<VacancyContact> Contacts { get; set; } = [];
    public string? Url { get; set; }
    public List<VacancyContact> StoredContacts { get; set; } = [];
    public string? StoredContactsHash { get; set; }
}

public sealed record PostProcessingResult
{
    public long? VacancyId { get; set; }
    public bool Created { get; set; }
    public int Before { get; set; }
    public int After { get; set; }
    public int Pages { get; set; }
    public List<string> NewContacts { get; set; } = [];
    public List<string> Failures { get; set; } = [];
}

public sealed record ProcessPost
{
    public Guid RequestId { get; set; }
    public PostProcessingStage Stage { get; set; }
    public int Version { get; set; } = 1;
}

public sealed class UnsupportedPostProcessingException() : Exception("unsupported post command or checkpoint");
public sealed class PostProcessingSupersededException() : Exception("post source or extraction recipe changed");
public sealed class PostContactsChangedException() : Exception("vacancy contacts changed before publication");

public static class PostWitness
{
    public static RawPost Capture(RawPost post) => new()
    {
        Id = post.Id, ProcessingRevision = post.ProcessingRevision, Source = post.Source, ChatRef = post.ChatRef, ChatTitle = post.ChatTitle,
        ExternalId = post.ExternalId, Url = post.Url, Author = post.Author, Text = post.Text, Links = [.. post.Links],
        PublishedAt = Normalize(post.PublishedAt), FetchedAt = Normalize(post.FetchedAt)!.Value, Status = post.Status, VacancyId = post.VacancyId,
    };
    private static DateTimeOffset? Normalize(DateTimeOffset? value) => value is { } date ? new DateTimeOffset(date.UtcTicks - date.UtcTicks % 10, TimeSpan.Zero) : null;
    public static string Hash(RawPost post) => CanonicalJson.Hash(JsonSerializer.Serialize(Capture(post), PostProcessingJsonContext.Default.RawPost));
    public static string ContactHash(IEnumerable<VacancyContact> contacts) => CanonicalJson.Hash(JsonSerializer.Serialize(contacts.ToList(), PostProcessingJsonContext.Default.ListVacancyContact));
    public static bool Matches(RawPost post, PostProcessingRequest request, PostProcessingInput input) => post.ProcessingRequestId == request.Id
        && post.ProcessingRevision == request.Revision && Hash(post) == Hash(input.Source);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(PostProcessingInput))]
[JsonSerializable(typeof(PostReading))]
[JsonSerializable(typeof(PostExtractionCheckpoint))]
[JsonSerializable(typeof(PostVerification))]
[JsonSerializable(typeof(PostProcessingResult))]
[JsonSerializable(typeof(ProcessPost))]
[JsonSerializable(typeof(RawPost))]
[JsonSerializable(typeof(List<VacancyContact>))]
public sealed partial class PostProcessingJsonContext : JsonSerializerContext;
