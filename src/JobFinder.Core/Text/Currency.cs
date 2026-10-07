namespace JobFinder.Core.Text;

public static class Currency
{
    /// <summary>Source spellings → one code: hh says "RUR", posts say "₽", "руб", "тр", "$", "бел. руб".</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = TextUtil.Normalize(raw).Trim('.', ' ');
        return s switch
        {
            "rur" or "rub" or "руб" or "₽" or "р" or "rubles" or "рублей" => "RUB",
            "byn" or "byr" or "br" or "бел. руб" or "бел.руб" or "белорусских рублей" or "byn/мес" => "BYN",
            "usd" or "$" or "долларов" or "dollars" => "USD",
            "eur" or "€" or "евро" => "EUR",
            "kzt" or "₸" or "тенге" => "KZT",
            _ => s.Length is >= 3 and <= 4 ? s.ToUpperInvariant() : null,
        };
    }
}
