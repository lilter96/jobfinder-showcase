using JobFinder.Core.Domain;

namespace JobFinder.Core.Text;

public static class EnglishLevels
{
    /// <summary>
    /// hh degree codes (a1..c2, l1 = native), CEFR spellings and the common named levels ("upper-intermediate", "fluent").
    /// Unknown wording is <see cref="EnglishLevel.Unknown"/>, never a guess.
    /// </summary>
    public static EnglishLevel Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return EnglishLevel.Unknown;
        var s = TextUtil.Normalize(raw).Replace('_', '-');
        return s switch
        {
            "a1" or "beginner" or "elementary" => EnglishLevel.A1,
            "a2" or "pre-intermediate" or "preintermediate" => EnglishLevel.A2,
            "b1" or "intermediate" => EnglishLevel.B1,
            "b2" or "upper-intermediate" or "upperintermediate" or "upper intermediate" => EnglishLevel.B2,
            "c1" or "advanced" or "fluent" => EnglishLevel.C1,
            "c2" or "proficiency" or "proficient" => EnglishLevel.C2,
            "l1" or "native" => EnglishLevel.Native,
            "none" or "not required" or "not-required" or "no" => EnglishLevel.NotRequired,
            _ => EnglishLevel.Unknown,
        };
    }

    /// <summary>Whether the level actually demands something (a known CEFR level).</summary>
    public static bool IsRequirement(EnglishLevel level) => level >= EnglishLevel.A1;
}
