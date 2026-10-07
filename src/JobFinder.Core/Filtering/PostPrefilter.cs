using System.Text.RegularExpressions;
using JobFinder.Core.Text;

namespace JobFinder.Core.Filtering;

public sealed class PrefilterOptions
{
    public const string Section = "Prefilter";

    /// <summary>At least one: .net, c#, dotnet, asp.net, шарп …</summary>
    public List<string> TechMarkers { get; set; } = [];

    /// <summary>At least one, matched as a word START (stems): "ваканс", "ищем", "hiring", "зп", "#vacancy" …</summary>
    public List<string> VacancyMarkers { get; set; } = [];

    /// <summary>Any of these (stems) rejects: "#резюме", "#ищуработу" — people looking for work, not offers.</summary>
    public List<string> RejectMarkers { get; set; } = [];

    public int MinLength { get; set; } = 80;
}

/// <summary>
/// Cheap regex gate in front of the LLM for Telegram/Threads posts. Most messages in job channels are for other stacks;
/// dropping them here keeps the LLM bill proportional to the .NET share of the traffic.
/// </summary>
public sealed class PostPrefilter
{
    private readonly PrefilterOptions _options;
    private readonly Regex[] _tech;
    private readonly Regex[] _vacancy;
    private readonly Regex[] _reject;

    public PostPrefilter(PrefilterOptions options)
    {
        _options = options;
        _tech = Build(options.TechMarkers, TextUtil.WholeWord);
        _vacancy = Build(options.VacancyMarkers, TextUtil.WordPrefix);
        _reject = Build(options.RejectMarkers, TextUtil.WordPrefix);
    }

    public bool LooksLikeDotnetVacancy(string? text, out string reason)
    {
        var normalized = TextUtil.Normalize(text);
        if (normalized.Length < _options.MinLength)
        {
            reason = "короткий пост";
            return false;
        }

        if (_reject.FirstOrDefault(r => r.IsMatch(normalized)) is { } rejected)
        {
            reason = $"стоп-маркер {rejected}";
            return false;
        }

        if (_tech.Length > 0 && !_tech.Any(r => r.IsMatch(normalized)))
        {
            reason = "нет .NET/C#";
            return false;
        }

        if (_vacancy.Length > 0 && !_vacancy.Any(r => r.IsMatch(normalized)))
        {
            reason = "нет признаков вакансии";
            return false;
        }

        reason = "";
        return true;
    }

    /// <summary>
    /// The gate for a short post that links out ("MIDDLE C#/.NET | ALFA-BANK &lt;telegra.ph&gt;", "Новая вакансия: &lt;link&gt;"): no stop marker,
    /// and a tech or a vacancy marker — worth reading its pages before the full check runs on post + pages.
    /// </summary>
    public bool MightBeVacancy(string? text)
    {
        var normalized = TextUtil.Normalize(text);
        return normalized.Length > 0 && !_reject.Any(r => r.IsMatch(normalized))
                                     && (_tech.Any(r => r.IsMatch(normalized)) || _vacancy.Any(r => r.IsMatch(normalized)));
    }

    /// <summary>Only the tech check: a short "Senior Java — hh.ru/vacancy/…" repost is not worth fetching the hh page.</summary>
    public bool HasTechMarker(string? text)
    {
        var normalized = TextUtil.Normalize(text);
        return _tech.Length == 0 || _tech.Any(r => r.IsMatch(normalized));
    }

    private static Regex[] Build(IEnumerable<string> words, Func<string, Regex> factory) =>
        words.Where(w => !string.IsNullOrWhiteSpace(w)).Select(factory).ToArray();
}
