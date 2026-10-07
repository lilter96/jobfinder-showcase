using System.Text;
using JobFinder.Core.Options;

namespace JobFinder.Pipeline.Documents;

/// <summary>
/// Fonts embedded into generated PDFs (<c>assets/fonts</c>: Inter subsets + <c>fonts.txt</c> with their unicode ranges). Embedding makes a
/// document render the same on Windows and in the Linux container, where only fallback fonts exist.
/// </summary>
public sealed class DocumentAssets(PathsOptions paths)
{
    private string? _fontCss;

    public string FontCss => _fontCss ??= BuildFontCss();

    private string BuildFontCss()
    {
        var dir = Path.GetFullPath(Path.Combine(paths.Assets, "fonts"));
        var index = Path.Combine(dir, "fonts.txt");
        if (!File.Exists(index)) return "";
        var css = new StringBuilder();
        foreach (var line in File.ReadAllLines(index))
        {
            var parts = line.Split('|', 2);
            if (parts.Length != 2) continue;
            var file = Path.Combine(dir, parts[0].Trim());
            if (!File.Exists(file)) continue;
            css.Append("@font-face{font-family:'Inter';font-style:normal;font-weight:100 900;font-display:block;")
                .Append("src:url(data:font/woff2;base64,").Append(Convert.ToBase64String(File.ReadAllBytes(file))).Append(") format('woff2');")
                .Append("unicode-range:").Append(parts[1].Trim()).Append(";}\n");
        }

        return css.ToString();
    }
}
