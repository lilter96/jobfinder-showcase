using System.Globalization;
using System.Text;
using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Llm;
using JobFinder.Llm.Tasks;

namespace JobFinder.Pipeline.Career;

/// <summary>
/// The interview copilot's code side: the evidence catalog the model may cite, the fixed questions, the check that a chosen case exists,
/// the speech check, the approval guardrail and the last-ten-minutes screen assembled from the answers. The model proposes, this decides.
/// </summary>
public static class CopilotRules
{
    public const string About = "about";
    public const string WhyCompany = "why_company";
    public const string WhyRole = "why_role";
    public const string SalaryId = "salary";
    public const string WhyHire = "why_hire";
    public const string FirstSteps = "first_steps";

    /// <summary>The questions every interview preparation answers for this company (the rest of HR lives in the HR document).</summary>
    public static IReadOnlyList<string> FixedIds => [.. Fixed.Select(f => f.Id)];

    /// <summary>Questions beyond the fixed ones: a shortlist, not a questionnaire.</summary>
    public const int MaxQuestions = 12;

    /// <summary>
    /// Part of every stored answer's key: stored answers already went through <see cref="Clean"/>, <see cref="SpeechProblems"/> and
    /// <see cref="Finish"/>, so a change there that should reach them bumps this.
    /// </summary>
    public const string RulesVersion = "1";

    private static readonly PlannedQuestion[] Fixed =
    [
        new()
        {
            Id = About, Question = "Расскажите о себе", Category = "hr", Priority = "critical", Likelihood = "high", Basis = "confirmed",
            Source = "начало почти любого интервью", Tests = "может ли коротко и по делу представить свой опыт под эту вакансию", Risk = "medium",
        },
        new()
        {
            Id = WhyCompany, Question = "Почему вы откликнулись именно к нам?", Category = "motivation", Priority = "high", Likelihood = "high",
            Basis = "likely", Source = "мотивация проверяется на первом же этапе", Tests = "осознанный ли выбор компании", Risk = "medium",
        },
        new()
        {
            Id = WhyRole, Question = "Что вы ищете в новой работе и почему эта роль?", Category = "motivation", Priority = "high",
            Likelihood = "medium", Basis = "likely", Source = "мотивация и совпадение ожиданий", Tests = "совпадают ли ожидания кандидата и роли",
            Risk = "medium",
        },
        new()
        {
            Id = WhyHire, Question = "Почему нам стоит нанять именно вас?", Category = "motivation", Priority = "high", Likelihood = "medium",
            Basis = "likely", Source = "ключевой довод под эту вакансию", Tests = "может ли связать свой опыт с задачами именно этой команды",
            Risk = "medium",
        },
        new()
        {
            Id = FirstSteps, Question = "Какими будут ваши первые шаги на новом месте?", Category = "motivation", Priority = "medium",
            Likelihood = "medium", Basis = "likely", Source = "senior-вопрос: как входить в систему и команду",
            Tests = "понимает ли, как войти в незнакомую систему и быстро дать результат", Risk = "medium",
        },
        new()
        {
            Id = SalaryId, Question = "Какие у вас зарплатные ожидания?", Category = "negotiation", Priority = "high", Likelihood = "high",
            Basis = "likely", Source = "зарплату спрашивают на первом этапе", Tests = "совпадает ли вилка", Risk = "high",
        },
    ];

    private static readonly string[] Technical = ["technical", "technical_depth", "system_design", "coding"];

    /// <summary>
    /// The reasoning tier a question needs: depth questions the maximum, technical ones high, behavior, motivation, money and the pitch
    /// medium, formalities low. <paramref name="effort"/> (low / medium / high / max) overrides.
    /// </summary>
    public static string PurposeFor(PlannedQuestion q, string? effort = null) => (effort ?? q.Category switch
    {
        "technical_depth" => "max",
        "technical" or "system_design" or "coding" => "high",
        "behavioral" or "motivation" or "negotiation" => "medium",
        _ => q.Id == About ? "medium" : "low",
    }) switch
    {
        "max" => LlmPurpose.Interview,
        "high" => LlmPurpose.InterviewHigh,
        "medium" => LlmPurpose.InterviewMedium,
        _ => LlmPurpose.InterviewLow,
    };

    /// <summary>A question waiting for the candidate's own position: no speech is invented, the options (or a text box) are shown.</summary>
    public static PrepItem PositionItem(PlannedQuestion q, IEnumerable<string> options, string reason) => new()
    {
        Q = q,
        A = new SpokenAnswer { EvidenceLevel = "personal_unknown", PositionOptions = [.. options] },
        RequiresApproval = true,
        CheckLevel = "blocker",
        ApprovalReasons = [reason],
    };

    public static int Rank(string? value) => value switch
    {
        "critical" => 0,
        "high" => 1,
        "medium" => 2,
        "low" => 3,
        _ => 4,
    };

    /// <summary>What the candidate has proven, with ids the model cites: S — stories, A — resume achievements, E — jobs.</summary>
    public static Dictionary<string, string> Catalog(CandidateProfile profile, IReadOnlyList<Story> stories)
    {
        var catalog = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < stories.Count; i++)
        {
            var s = stories[i];
            catalog[$"S{i + 1}"] = $"{s.Title}. Ситуация: {s.Situation} Задача: {s.Task} Действия: {s.Action} Результат: {s.Result}" +
                                   (string.IsNullOrWhiteSpace(s.Reflection) ? "" : $" Вывод: {s.Reflection}");
        }

        for (var i = 0; i < profile.Achievements.Count; i++) catalog[$"A{i + 1}"] = profile.Achievements[i];
        for (var i = 0; i < profile.Experience.Count; i++)
        {
            var e = profile.Experience[i];
            catalog[$"E{i + 1}"] = $"{e.Company}, {e.Role}, {e.Period}: {e.Highlights}";
        }

        return catalog;
    }

    public static string CatalogText(IReadOnlyDictionary<string, string> catalog) => string.Join("\n", catalog.Select(kv => $"[{kv.Key}] {kv.Value}"));

    /// <summary>
    /// The plan's shortlist made safe: the four fixed questions present (the model's wording kept when it gave one), ids unique, at most
    /// <see cref="MaxQuestions"/> others by priority, a case or evidence id the catalog does not have dropped with a reason for the card.
    /// </summary>
    public static (List<PlannedQuestion> Questions, Dictionary<string, List<string>> Reasons) Normalize(InterviewPlan plan,
        IReadOnlyDictionary<string, string> catalog)
    {
        var reasons = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var questions = new List<PlannedQuestion>();
        var counter = 0;
        foreach (var q in plan.Questions.Where(q => !string.IsNullOrWhiteSpace(q.Question)))
        {
            var id = string.IsNullOrWhiteSpace(q.Id) ? $"q{++counter}" : q.Id.Trim().ToLowerInvariant();
            while (!seen.Add(id)) id += "_";
            q.Id = id;
            q.Evidence = q.Evidence.Where(catalog.ContainsKey).Distinct().ToList();
            if (q.Story is { } story && !catalog.ContainsKey(story.Ref.Trim()))
            {
                Add(reasons, id, $"кейс {story.Ref} не найден в профиле: ответ без опоры");
                q.Story = null;
            }
            else if (q.Story is { } kept)
            {
                kept.Ref = kept.Ref.Trim();
            }

            questions.Add(q);
        }

        foreach (var f in Fixed.Where(f => !seen.Contains(f.Id))) questions.Add(f with { Evidence = [], Story = null });
        var fixedIds = Fixed.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var others = questions.Where(q => !fixedIds.Contains(q.Id))
            .OrderBy(q => Rank(q.Priority)).ThenBy(q => Rank(q.Likelihood)).Take(MaxQuestions);
        var result = questions.Where(q => fixedIds.Contains(q.Id)).OrderBy(q => Array.FindIndex(Fixed, f => f.Id == q.Id)).Concat(others).ToList();
        return (result, reasons);
    }

    /// <summary>The evidence for one question: its case and cited entries in full first, then the rest of the catalog.</summary>
    public static string EvidenceFor(PlannedQuestion q, IReadOnlyDictionary<string, string> catalog)
    {
        var chosen = new List<string>();
        if (q.Story is { } s && catalog.ContainsKey(s.Ref)) chosen.Add(s.Ref);
        chosen.AddRange(q.Evidence.Where(catalog.ContainsKey));
        chosen = chosen.Distinct().ToList();
        var sb = new StringBuilder();
        if (chosen.Count > 0)
        {
            sb.AppendLine("Опора этого вопроса:");
            foreach (var id in chosen) sb.Append('[').Append(id).Append("] ").AppendLine(catalog[id]);
            sb.AppendLine().AppendLine("Остальной подтверждённый опыт:");
        }

        foreach (var (id, text) in catalog.Where(kv => !chosen.Contains(kv.Key))) sb.Append('[').Append(id).Append("] ").AppendLine(text);
        return sb.ToString();
    }

    /// <summary>The typography the candidate reads aloud, on every spoken field.</summary>
    public static SpokenAnswer Clean(SpokenAnswer a)
    {
        a.Start = HumanText.Clean(a.Start.Trim());
        a.Core = HumanText.Clean(a.Core.Trim());
        a.Extended = string.IsNullOrWhiteSpace(a.Extended) ? null : HumanText.Clean(a.Extended.Trim());
        a.DeepDive = HumanText.Clean(a.DeepDive.Trim());
        foreach (var f in a.FollowUps)
        {
            f.Start = HumanText.Clean(f.Start.Trim());
            f.Core = HumanText.Clean(f.Core.Trim());
            f.Extended = string.IsNullOrWhiteSpace(f.Extended) ? null : HumanText.Clean(f.Extended.Trim());
        }

        foreach (var d in a.DoNotSay)
        {
            d.Say = HumanText.Clean(d.Say.Trim());
            d.Instead = HumanText.Clean(d.Instead.Trim());
        }

        a.FollowUps = a.FollowUps.Where(f => !string.IsNullOrWhiteSpace(f.Question) && !string.IsNullOrWhiteSpace(f.Start)).Take(5).ToList();
        return a;
    }

    /// <summary>What the speech check finds in every spoken part; an honest "не работал" is allowed only on a confirmed gap.</summary>
    public static List<string> SpeechProblems(SpokenAnswer a, PlannedQuestion q)
    {
        // No speech on purpose: only the positions are checked.
        if (a.NeedsPosition) return a.PositionOptions.Count(o => !string.IsNullOrWhiteSpace(o)) >= 2 ? [] : ["меньше двух вариантов позиции"];
        var gap = q.CandidateLevel == "gap";
        var problems = new List<string>();
        problems.AddRange(SpokenStyle.Problems(a.Start, SpokenStyle.Start, gap));
        problems.AddRange(SpokenStyle.Problems(a.Core, SpokenStyle.Core, gap));
        if (a.Extended is not null) problems.AddRange(SpokenStyle.Problems(a.Extended, SpokenStyle.Extended, gap));
        for (var i = 0; i < a.FollowUps.Count; i++)
        {
            var label = $"уточнение {i + 1}";
            problems.AddRange(SpokenStyle.Problems(a.FollowUps[i].Start, SpokenStyle.FollowUpStart with { Label = label + ", начало" }, gap));
            problems.AddRange(SpokenStyle.Problems(a.FollowUps[i].Core, SpokenStyle.FollowUpCore with { Label = label }, gap));
        }

        if (Technical.Contains(q.Category) && a.FollowUps.Count < 3) problems.Add("меньше трёх уточняющих вопросов");
        // "Instead" is what the candidate will say: the same live speech, no bookkeeping.
        foreach (var d in a.DoNotSay)
        {
            if (SpokenStyle.FindDisclaimer(d.Instead) is { } disclaimer) problems.Add($"«вместо»: оговорка «{disclaimer}»");
            if (ChatStyle.FindMeta(d.Instead) is { } meta) problems.Add($"«вместо»: пометка «{meta}»");
        }
        return problems;
    }

    /// <summary>
    /// Verify is for concrete inferred claims only: a bare technology name, a two-word label or a claim the sources state literally is
    /// noise and goes.
    /// </summary>
    public static List<string> CleanVerify(IEnumerable<string> verify, string sources)
    {
        var source = TextUtil.Normalize(sources);
        return verify.Select(v => v.Trim())
            .Where(v => v.Length >= 12 && v.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 2)
            .Where(v => !source.Contains(TextUtil.Normalize(v), StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The machine-enforced guardrail, in two levels. Blocker (the user decides): the candidate's own position, a depth question no source
    /// proves, negotiation, a gap or an unclear level, an unsupported answer, a failed speech check, a known reason (personal, money).
    /// Review (a glance): concrete episodes inferred from the experience (verify) in an otherwise derived answer.
    /// </summary>
    public static PrepItem Finish(PlannedQuestion q, SpokenAnswer? a, IEnumerable<string> style, IEnumerable<string> known, string sources)
    {
        var blockers = new List<string>(known);
        var reviews = new List<string>();
        var problems = style.ToList();
        if (a is null)
        {
            blockers.Add("ответ не собрался: пересоберите подготовку");
            return new PrepItem { Q = q, RequiresApproval = true, CheckLevel = "blocker", ApprovalReasons = blockers, StyleProblems = problems };
        }

        if (a.NeedsPosition)
        {
            blockers.Add("нужна ваша позиция: выберите вариант или напишите свой, ответ соберётся по нему");
        }
        else
        {
            a.Verify = CleanVerify(a.Verify, sources);
            if (q.Category == "technical_depth" && (q.Story is not { SupportedClaims.Count: > 0 } || a.Verify.Count > 0))
                blockers.Add("вопрос о глубине: источники не подтверждают уровень, проверьте, что готовы так сказать");
            if (a.Verify.Count > 0) reviews.Add("в ответе эпизоды, выведенные из опыта: сверьте, что так и было");
            if (q.Category == "negotiation") blockers.Add("переговоры: цифру и условия решаете вы");
            if (q.CandidateLevel == "gap") blockers.Add("пробел: проверьте, как вы это скажете");
            else if (q.CandidateLevel == "unknown" && Technical.Contains(q.Category)) blockers.Add("уровень по теме не ясен из профиля");
            if (a.EvidenceLevel == "unsupported") blockers.Add("данных мало даже для вывода");
            if (a.RequiresApproval)
                (a.EvidenceLevel == "derived" ? reviews : blockers).Add(string.IsNullOrWhiteSpace(a.ApprovalReason) ? "модель просит проверить" : a.ApprovalReason.Trim());
            if (problems.Count > 0) blockers.Add("речь не прошла проверку: " + string.Join("; ", problems.Take(3)));
        }

        var reasons = blockers.Concat(reviews).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new PrepItem
        {
            Q = q, A = a, RequiresApproval = reasons.Count > 0, ApprovalReasons = reasons, StyleProblems = problems,
            CheckLevel = blockers.Count > 0 ? "blocker" : reviews.Count > 0 ? "review" : "none",
        };
    }

    /// <summary>The level to show: builds from before the levels existed flagged everything the same way.</summary>
    public static string EffectiveCheck(PrepItem item) =>
        item.CheckLevel != "none" ? item.CheckLevel : item.RequiresApproval ? "blocker" : "none";

    /// <summary>The last ten minutes, from the answers themselves: nothing here is generated anew.</summary>
    public static LastMinuteBrief LastMinute(InterviewCopilot c)
    {
        SpokenAnswer? Of(string id) => c.Preparation.FirstOrDefault(p => p.Q.Id == id && p.HasSpeech)?.A;
        var ranked = c.Preparation.Where(p => p.HasSpeech).OrderBy(p => Rank(p.Q.Priority)).ThenBy(p => Rank(p.Q.Likelihood)).ToList();
        var technical = ranked.Where(p => Technical.Contains(p.Q.Category)).ToList();
        return new LastMinuteBrief
        {
            // The 30-second speech is the start and the core together: the core alone opens mid-thought ("Там я занимался…").
            About = Speech(Of(About)),
            WhyCompany = Speech(Of(WhyCompany)),
            WhyRole = Speech(Of(WhyRole)),
            TopStories = TopStories(c, ranked),
            TopRisks = c.Priorities.Where(p => p.CandidateLevel is "gap" or "unknown" || (p.Priority == "critical" && p.CandidateLevel != "strong"))
                .OrderBy(p => Rank(p.Priority)).Select(p => $"{p.Topic} ({Label(p.CandidateLevel)})")
                .Concat(c.Risks.Where(r => r.Severity == "high").Select(r => r.Risk)).Distinct().Take(3).ToList(),
            TopTopics = technical.Take(5).Select(p => new LastMinuteLine { Topic = p.Q.Question, Line = p.A!.Start }).ToList(),
            FollowUps = technical.Where(p => p.A!.FollowUps.Count > 0).Select(p => p.A!.FollowUps[0]).Take(5)
                .Select(f => new LastMinuteLine { Topic = f.Question, Line = f.Start }).ToList(),
            DoNotSay = ranked.Where(p => Rank(p.Q.Priority) <= 1).SelectMany(p => p.A!.DoNotSay)
                .Concat(c.Salary?.A?.DoNotSay ?? []).Where(d => !string.IsNullOrWhiteSpace(d.Say))
                .DistinctBy(d => TextUtil.Normalize(d.Say)).Take(5).ToList(),
        };
    }

    private static void Add(Dictionary<string, List<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        list.Add(value);
    }

    private static string Speech(SpokenAnswer? a) => a is null ? "" : $"{a.Start} {a.Core}".Trim();

    /// <summary>
    /// Three cases to have ready: the ones the answers lean on, concrete episodes (stories, achievements) before whole jobs, filled up
    /// with the resume's first achievements when the answers use fewer.
    /// </summary>
    private static List<LastMinuteStory> TopStories(InterviewCopilot c, List<PrepItem> ranked)
    {
        var chosen = ranked.Select(p => p.Q.Story).OfType<StoryChoice>().Where(s => c.Evidence.ContainsKey(s.Ref))
            .OrderBy(s => s.Ref.StartsWith('E') ? 1 : 0).DistinctBy(s => s.Ref)
            .Select(s => new LastMinuteStory
            {
                // The result is what to say; why the case was chosen is a note for the plan, not for the last minutes.
                Ref = s.Ref, Title = TextUtil.Truncate(c.Evidence[s.Ref], 140), Line = s.Result,
            })
            .Where(s => !s.Ref.StartsWith('E')).Take(3).ToList();
        foreach (var (id, text) in c.Evidence.Where(kv => kv.Key.StartsWith('A') && chosen.All(s => s.Ref != kv.Key)))
        {
            if (chosen.Count >= 3) break;
            chosen.Add(new LastMinuteStory { Ref = id, Title = TextUtil.Truncate(text, 140), Line = "" });
        }

        return chosen;
    }

    public static string Label(string? value) => value switch
    {
        "critical" => "критично",
        "high" => "высокий",
        "medium" => "средний",
        "low" => "низкий",
        "strong" => "сильная сторона",
        "adequate" => "уверенно",
        "gap" => "пробел",
        "unknown" => "не ясно",
        "confirmed" => "из вакансии/переписки",
        "likely" => "вероятно",
        "speculative" => "гипотеза",
        "hr" => "HR",
        "motivation" => "мотивация",
        "technical" => "тех",
        "technical_depth" => "глубина",
        "system_design" => "system design",
        "coding" => "код",
        "behavioral" => "поведенческий",
        "negotiation" => "переговоры",
        null => "",
        _ => value,
    };

    public static string Css(string? value) => value switch
    {
        "critical" or "gap" or "high" => "bad",
        "medium" or "unknown" => "warn",
        "strong" or "low" => "good",
        _ => "",
    };

    public static string Seconds(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? ""
            : $"~{Math.Max(5, (int)Math.Round(text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length / 2.5 / 5.0) * 5).ToString(CultureInfo.InvariantCulture)} сек";
}
