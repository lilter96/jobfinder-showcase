using System.Text.RegularExpressions;

namespace JobFinder.Llm.Prompts;

/// <summary>
/// Prompts are data: <c>prompts/*.md</c>, loaded once at startup (a missing file stops the start with its name). Placeholders are
/// <c>{{name}}</c>; an unknown placeholder left in a rendered prompt is a bug, so rendering fails loudly instead of sending it.
/// </summary>
public sealed partial class PromptCatalog
{
    public static IReadOnlyList<string> Required { get; } = Array.AsReadOnly(Tasks.LlmTaskCatalog.All.Select(task => task.Prompt).Distinct(StringComparer.Ordinal).ToArray());

    private readonly Dictionary<string, string> _prompts;

    public PromptCatalog(string directory)
    {
        _prompts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var name in Required)
        {
            var path = Path.Combine(directory, name + ".md");
            if (!File.Exists(path))
            {
                missing.Add(path);
                continue;
            }

            _prompts[name] = File.ReadAllText(path).Trim();
        }

        if (missing.Count > 0) throw new FileNotFoundException($"prompt files missing: {string.Join(", ", missing)}");
    }

    internal PromptCatalog(Dictionary<string, string> prompts) => _prompts = prompts;

    /// <summary>The raw template (for versioning what was generated with it).</summary>
    public string Template(string name) =>
        _prompts.TryGetValue(name, out var template) ? template : throw new KeyNotFoundException($"prompt {name} not loaded");

    public string Render(string name, IReadOnlyDictionary<string, string> values)
    {
        if (!_prompts.TryGetValue(name, out var template)) throw new KeyNotFoundException($"prompt {name} not loaded");
        // Checked on the template, not on the result: an inserted vacancy text may itself contain "{{…}}".
        foreach (Match m in Placeholder().Matches(template))
        {
            if (!values.ContainsKey(m.Groups[1].Value)) throw new InvalidOperationException($"prompt {name}: no value for {m.Value}");
        }

        return Placeholder().Replace(template, m => values[m.Groups[1].Value]);
    }

    [GeneratedRegex(@"\{\{([a-zA-Z0-9_]+)\}\}")]
    private static partial Regex Placeholder();
}
