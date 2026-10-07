using System.Text.Json.Serialization;

namespace JobFinder.Core.Domain;

/// <summary>
/// One way to reach the employer about a vacancy, as found: the kind, the normalized value, what the source called it and where it was.
/// Stored as jsonb on the vacancy (setters only: see JsonDefaultsTests).
/// </summary>
public sealed class VacancyContact
{
    [JsonConverter(typeof(JsonStringEnumConverter<ContactKind>))]
    public ContactKind Kind { get; set; }

    /// <summary>
    /// Normalized: "@username" in lower case (or "tg://user?id=…" for a person without one), an e-mail in lower case, a phone in E.164,
    /// an absolute URL for <see cref="ContactKind.Link"/> and LinkedIn.
    /// </summary>
    public string Value { get; set; } = "";

    /// <summary>What the source called it: the link text ("Откликнуться", "Посмотреть контакт"), a button, a person's name.</summary>
    public string? Label { get; set; }

    /// <summary>The page it was found on; null = the post itself.</summary>
    public string? Source { get; set; }

    public override string ToString() => Label is null ? Value : $"{Value} ({Label})";
}

/// <summary>
/// A link of a post that its text does not show: a Telegram text link («Откликнуться» → URL), a URL button, a mention of a person
/// without a public username. Plain URLs of the text are kept too, exactly as Telegram marked them.
/// </summary>
public sealed class PostLink
{
    public string Url { get; set; } = "";

    /// <summary>The text the link sits on (or the button caption); null for a bare URL.</summary>
    public string? Label { get; set; }
}
