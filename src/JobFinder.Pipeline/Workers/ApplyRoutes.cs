using System.Text.RegularExpressions;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Integrations.Email;
using JobFinder.Pipeline.Profile;

namespace JobFinder.Pipeline.Workers;

/// <summary>How a vacancy can be answered: hh form, a DM to the recruiter, an e-mail, or only by hand (<see cref="Channel"/> null).</summary>
public sealed record ApplyRoute(ApplicationChannel? Channel, string? Target)
{
    public static readonly ApplyRoute Manual = new(null, null);

    /// <summary>The route sends the resume file along (a DM or an e-mail; hh uses the resume stored in the account).</summary>
    public bool AttachesResume => Channel is ApplicationChannel.TelegramDm or ApplicationChannel.Email;
}

/// <summary>
/// The one place that decides the route, in order of reliability: the hh form (structured, the account's resume), a Telegram DM
/// (fast, read), an e-mail. The bot card, the approval action and the letter style all follow the same answer.
/// </summary>
public static partial class ApplyRoutes
{
    public static ApplyRoute Resolve(Vacancy v, bool emailConfigured)
    {
        if (HhApplicable(v)) return new ApplyRoute(ApplicationChannel.HeadHunter, null);
        if (TelegramContact(v.Contacts) is { } dm) return new ApplyRoute(ApplicationChannel.TelegramDm, dm);
        if (emailConfigured && EmailContact(v.Contacts) is { } mail) return new ApplyRoute(ApplicationChannel.Email, mail);
        return ApplyRoute.Manual;
    }

    public static bool HasHh(Vacancy v) => v.Sources.Any(s => s.Source == SourceKind.HeadHunter);

    /// <summary>The browser can answer it: an hh source without an employer test.</summary>
    public static bool HhApplicable(Vacancy v) => v.Sources.Any(s => s.Source == SourceKind.HeadHunter && !s.HasEmployerTest);

    /// <summary>
    /// The hh vacancy to answer. A merged vacancy can carry several hh postings: one without an employer test goes first, the one
    /// <see cref="HhApplicable"/> vouched for — the first posting found may be the one with the test.
    /// </summary>
    public static long? HhId(Vacancy v) =>
        v.Sources.Where(s => s.Source == SourceKind.HeadHunter).OrderBy(s => s.HasEmployerTest).Select(s => long.TryParse(s.ExternalId, out var id) ? id : (long?)null).FirstOrDefault();

    /// <summary>
    /// The first person on Telegram among the contacts (a recruiter to write to): an @username, in the source's order. A bot or a person
    /// known only by id is not reachable by a DM from here.
    /// </summary>
    public static string? TelegramContact(IEnumerable<VacancyContact> contacts) =>
        contacts.FirstOrDefault(c => c.Kind == ContactKind.Telegram && c.Value.StartsWith('@'))?.Value;

    public static string? EmailContact(IEnumerable<VacancyContact> contacts) => contacts.FirstOrDefault(c => c.Kind == ContactKind.Email)?.Value;

    /// <summary>The resume file and the name the recipient sees: "Имя — Должность.pdf", not "resume.pdf".</summary>
    public static EmailAttachment? Resume(ProfileProvider profiles, CandidateProfile? profile)
    {
        var path = profiles.FindResume();
        if (path is null) return null;
        var baseName = string.Join(" — ", new[] { profile?.Name, profile?.Title }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (baseName.Length == 0) baseName = "Резюме";
        var safe = string.Concat(baseName.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' or ':' ? ' ' : c));
        // "C# / .NET" → "C# .NET", not "C#   .NET".
        safe = Spaces().Replace(safe, " ").Trim();
        return new EmailAttachment(path, safe + Path.GetExtension(path));
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
