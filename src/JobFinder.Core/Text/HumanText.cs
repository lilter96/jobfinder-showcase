namespace JobFinder.Core.Text;

/// <summary>
/// Texts that go out under the user's name (comments, letters, DMs, replies) must not carry typography nobody types on a phone: the
/// em and en dashes and the one-character ellipsis give a generated text away at a glance. Applied where a model writes such a text and
/// again at every point it is sent, so a letter saved before this rule still goes out clean.
/// </summary>
public static class HumanText
{
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        return text
            .Replace(" — ", " - ", StringComparison.Ordinal)
            .Replace(" – ", " - ", StringComparison.Ordinal)
            .Replace('—', '-')
            .Replace('–', '-')
            .Replace("…", "...", StringComparison.Ordinal);
    }

    public static bool IsClean(string? text) => text is null || text.IndexOfAny(['—', '–', '…']) < 0;
}
