using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace JobFinder.Core.Text;

public static partial class TextUtil
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Lower case, ё → е, whitespace collapsed. The form every keyword comparison uses.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var lowered = text.ToLower(Ru).Replace('ё', 'е');
        return Whitespace().Replace(lowered, " ").Trim();
    }

    public static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= max ? text : string.Concat(text.AsSpan(0, max - 1), "…");
    }

    /// <summary>
    /// HTML → readable plain text: block tags become line breaks, list items get a bullet, entities are decoded.
    /// Enough for vacancy descriptions (hh, Habr); not a general HTML renderer.
    /// </summary>
    public static string HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var s = LineBreakTags().Replace(html, "\n");
        s = ListItemTag().Replace(s, "\n• ");
        s = BlockCloseTags().Replace(s, "\n");
        s = AnyTag().Replace(s, "");
        s = WebUtility.HtmlDecode(s).Replace(' ', ' ');
        var sb = new StringBuilder(s.Length);
        var blank = 0;
        foreach (var raw in s.Split('\n'))
        {
            var line = SpacesAndTabs().Replace(raw, " ").Trim();
            if (line.Length == 0)
            {
                if (++blank > 1 || sb.Length == 0) continue;
            }
            else
            {
                blank = 0;
            }

            sb.Append(line).Append('\n');
        }

        return sb.ToString().Trim();
    }

    /// <summary>Telegram HTML parse mode escaping (only &lt;, &gt;, &amp; are special).</summary>
    public static string EscapeHtml(string? text) =>
        string.IsNullOrEmpty(text) ? "" : text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>
    /// A regex that matches <paramref name="word"/> as a whole token: no letter or digit right before or after it.
    /// Unlike \b it works for tokens that start or end with a symbol (".net", "c#", "1с").
    /// </summary>
    public static Regex WholeWord(string word) =>
        new($@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(Normalize(word))}(?![\p{{L}}\p{{N}}])", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// A regex that matches a token STARTING with <paramref name="stem"/>: "ваканс" finds "вакансия", "вакансии", "вакансию".
    /// Russian inflects, so keyword lists of the prefilter are stems, not words.
    /// </summary>
    public static Regex WordPrefix(string stem) =>
        new($@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(Normalize(stem))}", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[ \t\r\f\v]+")]
    private static partial Regex SpacesAndTabs();

    [GeneratedRegex(@"<\s*br\s*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTags();

    [GeneratedRegex(@"<\s*li[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItemTag();

    // </li> is not here: <li> already starts a new bullet line, closing it again would leave a blank line between items.
    [GeneratedRegex(@"</\s*(p|div|ul|ol|h[1-6]|tr|blockquote)\s*>|<\s*(p|div|h[1-6]|tr|blockquote)(\s[^>]*)?>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockCloseTags();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTag();
}
