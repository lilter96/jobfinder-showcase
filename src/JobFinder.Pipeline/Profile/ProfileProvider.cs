using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using DocumentFormat.OpenXml.Packaging;
using JobFinder.Core.Options;
using JobFinder.Core.Profile;
using JobFinder.Core.Storage;
using JobFinder.Pipeline.Modules.Candidate;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace JobFinder.Pipeline.Profile;

/// <summary>
/// The candidate as every prompt sees it: the document <see cref="Key"/> in the database, edited on the dashboard (Настройки) and written by
/// <c>--import-resume</c>. A change from any process applies to the next vacancy without a restart. No profile = no scoring: the workers
/// wait instead of scoring against nothing. The resume itself stays a file (<c>profile/resume.pdf</c>): it is what the user supplies.
/// </summary>
public sealed class ProfileProvider(IDocumentStore documents, PathsOptions paths, ILogger<ProfileProvider> log, ICandidatePublisher publisher)
{
    public const string Key = "candidate.profile";

    /// <summary>The profile before the last <c>--import-resume</c> (what the user wrote by hand is never lost to a re-import).</summary>
    public const string PreviousKey = "candidate.profile.previous";

    private static readonly string[] ResumeNames = ["resume.pdf", "resume.docx", "resume.md", "resume.txt"];

    // Read by people too (the dashboard editor, psql): Cyrillic stays readable instead of \u escapes. The naming policy must be the
    // context's own: options handed to a source-generated context name the properties themselves, and "Name" does not read back as "name".
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = ProfileJsonContext.Default,
    };

    private static readonly JsonTypeInfo<CandidateProfile> Write = (JsonTypeInfo<CandidateProfile>)WriteOptions.GetTypeInfo(typeof(CandidateProfile));

    private readonly DocumentCache<CandidateProfile> _profile = new(documents, Key, ProfileJsonContext.Default.CandidateProfile, log, Write);

    public Task<CandidateProfile?> GetAsync(CancellationToken ct) => _profile.GetAsync(ct);

    public async Task<VersionedDocument<CandidateProfile>?> GetSnapshotAsync(CancellationToken ct)
    {
        var document = await documents.GetAsync(Key, ct);
        if (document is null) return null;
        return new VersionedDocument<CandidateProfile>(FromJson(document.Json), document.Version);
    }

    public async Task<VersionedDocument<CandidateProfile>> SaveAsync(CandidateProfile profile, long expectedVersion, string actor, CancellationToken ct,
        bool keepPrevious = false)
    {
        var saved = await publisher.PublishProfileAsync(profile, expectedVersion, actor, ct, keepPrevious);
        log.Info($"[profile] saved v{saved.Version} ({saved.Value.Skills.Count} skills, reviewed: {saved.Value.Reviewed})");
        return saved;
    }

    /// <summary>The profile as the dashboard editor shows it: indented, fields in their declared order.</summary>
    public static string ToJson(CandidateProfile profile) => JsonSerializer.Serialize(profile, Write);

    /// <summary>The editor's text as a profile; a <see cref="JsonException"/> names what does not parse.</summary>
    public static CandidateProfile FromJson(string json) =>
        JsonSerializer.Deserialize(json, ProfileJsonContext.Default.CandidateProfile) ?? throw new JsonException("пустой профиль");

    private readonly Lock _lock = new();
    private string? _resumeText;
    private (string Path, DateTime Mtime) _resumeKey;

    /// <summary>The base resume as text (cached until the file changes): the fact source of tailored resumes and stories.</summary>
    public string? ResumeText()
    {
        var path = FindResume();
        if (path is null) return null;
        var key = (path, File.GetLastWriteTimeUtc(path));
        lock (_lock)
        {
            if (_resumeText is not null && _resumeKey == key) return _resumeText;
            _resumeText = ReadResumeText(path);
            _resumeKey = key;
            return _resumeText;
        }
    }

    public string? FindResume()
    {
        var dir = Path.GetFullPath(paths.Profile);
        return ResumeNames.Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists);
    }

    /// <summary>Plain text of a PDF, DOCX, Markdown or text resume.</summary>
    public static string ReadResumeText(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        switch (ext)
        {
            case ".pdf":
            {
                using var pdf = PdfDocument.Open(path);
                // Content-order extraction keeps words and lines apart (page.Text glues them together).
                return string.Join("\n\n", pdf.GetPages().Select(p => ContentOrderTextExtractor.GetText(p)));
            }
            case ".docx":
            {
                using var doc = WordprocessingDocument.Open(path, false);
                var body = doc.MainDocumentPart?.Document?.Body;
                if (body is null) return "";
                return string.Join("\n", body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>().Select(p => p.InnerText));
            }
            default:
                return File.ReadAllText(path);
        }
    }
}
