using System.Text.RegularExpressions;
using JobFinder.Core.Domain;
using JobFinder.Core.Options;
using JobFinder.Core.Text;

namespace JobFinder.Core.Filtering;

public readonly record struct FilterVerdict(bool Passed, string? Reason)
{
    public static FilterVerdict Pass => new(true, null);

    public static FilterVerdict Reject(string reason) => new(false, reason);
}

/// <summary>
/// Deterministic rules that run before the LLM: they are free, explainable (the reason is stored and shown) and never
/// hallucinate. Anything uncertain passes — the LLM score decides it; only a clear violation is rejected here.
/// </summary>
public sealed class HardFilter
{
    private readonly FilterOptions _options;
    private readonly (string Word, Regex Regex)[] _stopWords;
    private readonly Regex[] _markers;
    private readonly HashSet<string> _officeCities;
    private readonly HashSet<string> _remoteCountries;
    private readonly string[] _blacklist;

    public HardFilter(FilterOptions options)
    {
        _options = options;
        _stopWords = options.TitleStopWords.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => (w, TextUtil.WholeWord(w))).ToArray();
        _markers = options.RequiredMarkers.Where(w => !string.IsNullOrWhiteSpace(w)).Select(TextUtil.WholeWord).ToArray();
        _officeCities = options.OfficeCities.Select(TextUtil.Normalize).ToHashSet(StringComparer.Ordinal);
        _remoteCountries = options.RemoteCountries.Select(c => c.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        _blacklist = options.CompanyBlacklist.Select(VacancyFingerprint.NormalizeCompany).Where(c => c.Length > 0).ToArray();
    }

    public FilterVerdict Check(Vacancy vacancy, bool alreadyApplied)
    {
        if (alreadyApplied) return FilterVerdict.Reject("уже есть отклик на эту вакансию");

        var title = TextUtil.Normalize(vacancy.Title);
        foreach (var (word, regex) in _stopWords)
        {
            if (regex.IsMatch(title)) return FilterVerdict.Reject($"стоп-слово в названии: «{word}»");
        }

        if (_markers.Length > 0 && !HasMarker(vacancy)) return FilterVerdict.Reject("нет .NET/C# ни в названии, ни в навыках, ни в описании");

        var geo = CheckGeography(vacancy);
        if (!geo.Passed) return geo;

        if (EnglishLevels.IsRequirement(vacancy.EnglishRequired) && vacancy.EnglishRequired > _options.MaxEnglish)
            return FilterVerdict.Reject($"требуется английский {vacancy.EnglishRequired} (максимум {_options.MaxEnglish})");

        var company = VacancyFingerprint.NormalizeCompany(vacancy.CompanyName);
        if (company.Length > 0 && _blacklist.Any(b => company.Contains(b, StringComparison.Ordinal)))
            return FilterVerdict.Reject($"компания в чёрном списке: {vacancy.CompanyName}");

        return FilterVerdict.Pass;
    }

    private bool HasMarker(Vacancy vacancy)
    {
        var haystacks = new[] { vacancy.Title, string.Join(' ', vacancy.Skills), vacancy.Description };
        foreach (var raw in haystacks)
        {
            var text = TextUtil.Normalize(raw);
            if (text.Length == 0) continue;
            if (_markers.Any(m => m.IsMatch(text))) return true;
        }

        return false;
    }

    /// <summary>
    /// Office/hybrid only in the configured cities; remote only for employers in the configured countries. An unknown format or
    /// country passes (the LLM sees the text); a known mismatch — "office in Moscow, no remote" — is rejected.
    /// </summary>
    private FilterVerdict CheckGeography(Vacancy v)
    {
        var city = TextUtil.Normalize(v.City);
        var inOfficeCity = city.Length > 0 && _officeCities.Contains(city);
        var countryOk = v.CountryCode is null || _remoteCountries.Count == 0 || _remoteCountries.Contains(v.CountryCode.ToUpperInvariant());

        if (v.WorkFormats == WorkFormat.None) return FilterVerdict.Pass;
        if (v.WorkFormats.HasFlag(WorkFormat.Remote) && countryOk) return FilterVerdict.Pass;
        if ((v.WorkFormats & (WorkFormat.Office | WorkFormat.Hybrid)) != 0 && (inOfficeCity || city.Length == 0 && _officeCities.Count == 0))
            return FilterVerdict.Pass;

        var where = string.IsNullOrWhiteSpace(v.City) ? v.CountryCode ?? "?" : v.City;
        return v.WorkFormats.HasFlag(WorkFormat.Remote)
            ? FilterVerdict.Reject($"удалёнка для компании из {v.CountryCode} (разрешены {string.Join(", ", _remoteCountries)})")
            : FilterVerdict.Reject($"только офис/гибрид в {where}, удалёнки нет");
    }
}
