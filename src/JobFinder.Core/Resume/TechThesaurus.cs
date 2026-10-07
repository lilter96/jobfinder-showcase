using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using JobFinder.Core.Text;

namespace JobFinder.Core.Resume;

/// <summary>
/// What a .NET backend resume may truthfully claim beyond its literal words. Two relations, both conservative:
/// <list type="bullet">
/// <item>names of one thing («MS SQL Server», «SQL Server», «MSSQL»; «микросервисы», «Microservices») — any of them in the source is the
/// same fact;</item>
/// <item>knowledge that comes with a skill (five years of ASP.NET Core means dependency injection and Web API; MS SQL Server means T-SQL;
/// a GitHub profile means Git) — what an interviewer would take for granted.</item>
/// </list>
/// An ATS matches words, not facts: <see cref="Visible"/> is what it finds in the resume (any word form, any name of the same thing),
/// <see cref="Proven"/> is what the source allows to write. A vacancy term proven but not visible is what the writer must add in the
/// vacancy's own words.
/// </summary>
public static partial class TechThesaurus
{
    private static readonly string[][] Same =
    [
        ["MS SQL Server", "MS SQL", "SQL Server", "MSSQL", "Microsoft SQL Server"],
        ["PostgreSQL", "Postgres"],
        ["REST API", "RESTful API", "REST"],
        ["Web API", "ASP.NET Web API", "ASP.NET Core Web API"],
        ["микросервисы", "микросервисная архитектура", "Microservices", "microservice architecture"],
        ["высоконагруженные системы", "Highload", "high-load", "high load", "высокая нагрузка", "высоконагруженные сервисы"],
        ["распределённые системы", "Distributed Systems"],
        ["Apache Kafka", "Kafka"],
        ["Kubernetes", "K8s"],
        ["Entity Framework Core", "EF Core", "Entity Framework"],
        ["CI/CD", "CI / CD"],
        ["Event-Driven Architecture", "event-driven", "событийно-ориентированная архитектура"],
        ["Dependency Injection", "DI", "IoC", "IoC-контейнеры", "IoC-контейнер", "IoC container", "внедрение зависимостей"],
        ["unit-тесты", "юнит-тесты", "модульные тесты", "unit tests", "unit testing", "модульное тестирование"],
        ["интеграционные тесты", "integration-тесты", "integration tests", "интеграционное тестирование"],
        ["управление памятью", "Memory Management", "оптимизация памяти", "управление памятью .NET"],
        ["OpenTelemetry", "OTel"],
        ["C#", "C Sharp"],
    ];

    private static readonly (string Has, string[] Implies)[] Implied =
    [
        ("MS SQL Server", ["T-SQL", "SQL"]),
        ("PostgreSQL", ["SQL"]),
        ("ASP.NET Core", ["Web API", "Dependency Injection", "middleware"]),
        ("REST API", ["Web API"]),
        ("GitHub", ["Git"]),
        ("GitLab", ["Git"]),
        ("xUnit", ["unit-тесты"]),
        ("Testcontainers", ["интеграционные тесты"]),
        ("Transactional Outbox", ["Event-Driven Architecture"]),
        ("ArrayPool", ["управление памятью"]),
        ("Span<T>", ["управление памятью"]),
    ];

    private static readonly ConcurrentDictionary<string, Regex> Patterns = new(StringComparer.Ordinal);

    /// <summary>A text prepared once for many lookups: lower case, and the stems of its words.</summary>
    public sealed class Text(string text)
    {
        private readonly string _normalized = TextUtil.Normalize(text);

        public IReadOnlySet<string> Stems { get; } = JobFinder.Core.Text.Stems.Of(text).ToHashSet(StringComparer.Ordinal);

        /// <summary>
        /// The term itself in any word form: a technology name as a whole word (case, spaces and hyphens aside: «MSSQL» ≈ «MS SQL»; «DI» is not
        /// found in «Redis»), a Russian phrase by the stems of its words.
        /// </summary>
        public bool Has(string term)
        {
            if (TextUtil.Normalize(term).Length == 0) return false;
            if (IsTechName(term)) return Pattern(term).IsMatch(_normalized);
            return JobFinder.Core.Text.Stems.Covers(Stems, term) || Pattern(term).IsMatch(_normalized);
        }
    }

    /// <summary>"ASP.NET Core" → a whole-word pattern where spaces and hyphens between the parts are optional.</summary>
    private static Regex Pattern(string term) => Patterns.GetOrAdd(TextUtil.Normalize(term), t =>
    {
        var body = string.Join(@"[\s-]*", Regex.Split(t, @"[\s-]+").Where(p => p.Length > 0).Select(Regex.Escape));
        return new Regex($@"(?<![\p{{L}}\p{{N}}#+.]){body}(?![\p{{L}}\p{{N}}#+])", RegexOptions.CultureInvariant);
    });

    /// <summary>The term or another name of the same thing is in the text: what a keyword search finds.</summary>
    public static bool Visible(string term, Text text) => Names(term).Any(text.Has);

    /// <summary>The source allows to claim the term: it names it (any name) or a skill that implies it.</summary>
    public static bool Proven(string term, Text source)
    {
        if (Visible(term, source)) return true;
        // "MS SQL Server (T-SQL)", "Redis (distributed locks, rate limiting)": proven when every part is.
        if (Composite().Match(term) is { Success: true } m)
        {
            var parts = new[] { m.Groups[1].Value }.Concat(m.Groups[2].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return parts.All(p => Proven(p, source));
        }

        // "Transactional Outbox/Inbox", "PostgreSQL/MS SQL": proven when every alternative is ("CI/CD" is proven whole, above).
        var alternatives = Alternatives(term);
        if (alternatives.Length > 1 && alternatives.All(a => a.Length >= 2) && alternatives.All(a => Proven(a, source))) return true;

        // Through any name of the term: «IoC-контейнеры» is «Dependency Injection», which ASP.NET Core implies.
        var names = Names(term).ToList();
        return Implied.Any(i => i.Implies.Any(t => names.Any(n => SameThing(t, n))) && Visible(i.Has, source));
    }

    /// <summary>
    /// What the source implies, for the writer and the reviewer ("ASP.NET Core → Web API, Dependency Injection (IoC-контейнеры), middleware"):
    /// the same rules the gate applies, said once to both.
    /// </summary>
    public static string ImpliedBy(Text source)
    {
        var lines = Implied.Where(i => Visible(i.Has, source))
            .Select(i => $"{i.Has} → " + string.Join(", ", i.Implies.Select(t =>
            {
                var other = Same.FirstOrDefault(g => g.Any(n => SameThing(n, t)))?.FirstOrDefault(n => !SameThing(n, t) && !IsTechName(n));
                return other is null ? t : $"{t} ({other})";
            })));
        return string.Join("; ", lines);
    }

    /// <summary>
    /// "A/B" as its alternatives; a suffix after a hyphen on the last one is shared ("Unit/Integration-тесты" → "Unit-тесты",
    /// "Integration-тесты").
    /// </summary>
    public static string[] Alternatives(string term)
    {
        var parts = term.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return parts;
        var dash = parts[^1].IndexOf('-', StringComparison.Ordinal);
        if (dash <= 0 || parts[..^1].Any(p => p.Contains('-', StringComparison.Ordinal))) return parts;
        var suffix = parts[^1][dash..];
        return [.. parts[..^1].Select(p => p + suffix), parts[^1]];
    }

    /// <summary>The term and the other names of the same thing.</summary>
    public static IEnumerable<string> Names(string term)
    {
        yield return term;
        foreach (var group in Same.Where(g => g.Any(n => SameThing(n, term))))
            foreach (var n in group)
                yield return n;
    }

    /// <summary>Two names of one thing: equal technology forms, or the same words in any form («IoC-контейнеры» ≈ «IoC-контейнеров»).</summary>
    public static bool SameThing(string a, string b)
    {
        var ta = ResumeFaithfulness.TechForm(a);
        var tb = ResumeFaithfulness.TechForm(b);
        if (ta.Length == 0 || tb.Length == 0) return false;
        if (ta == tb) return true;
        if (IsTechName(a) || IsTechName(b)) return false;
        var sa = JobFinder.Core.Text.Stems.Of(a);
        var sb = JobFinder.Core.Text.Stems.Of(b);
        return sa.Count > 0 && sa.SequenceEqual(sb);
    }

    [GeneratedRegex(@"^\s*([^()]+?)\s*\(([^()]+)\)\s*$")]
    private static partial Regex Composite();

    /// <summary>A technology name (Latin letters, digits, # + . /) rather than a Russian phrase: matched as written, not by word stems.</summary>
    public static bool IsTechName(string term) => !term.Any(c => c is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё');
}
