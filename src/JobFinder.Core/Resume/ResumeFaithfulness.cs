using System.Globalization;
using System.Text.RegularExpressions;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;

namespace JobFinder.Core.Resume;

/// <param name="Violations">What the model is told to fix on a rewrite.</param>
/// <param name="Corrections">What <c>fix</c> changed in the document.</param>
/// <param name="Covered">Vacancy terms a keyword search finds in the text.</param>
/// <param name="Missing">Vacancy terms the candidate does not have (by the source): gaps, never written into the resume.</param>
/// <param name="Unshown">Vacancy terms the candidate has (by the source) that the text does not show yet: to be written in the vacancy's words.</param>
public sealed record FaithfulnessReport(ResumeDocument Document, List<string> Violations, List<string> Corrections, List<string> Covered,
    List<string> Missing, List<string> Unshown);

/// <summary>
/// The code-level gate for tailored resumes: honest and clean. A tailored resume may rephrase, reorder and use the vacancy's words for
/// the same facts, never invent: every number must exist in the source facts (resume text + confirmed stories), every company, role and
/// period is the source's, no job is dropped, every listed skill is in the source or follows from it (<see cref="TechThesaurus"/>) — an
/// unproven one survives only as "familiar", and only when the vacancy asks for it. And the resume talks about what the candidate has:
/// a sentence about a gap, the vacancy or the source ("в предоставленном опыте не указаны") is removed wherever it appears.
/// <see cref="Check"/> reports; with <c>fix</c> it also repairs the document.
/// </summary>
public static partial class ResumeFaithfulness
{
    public const int MaxFamiliar = 3;

    public const int MaxBulletLength = 260;

    public const int MaxSummaryLength = 650;

    public const int MaxSummarySentences = 4;

    /// <param name="sourceJobs">The profile's jobs: the truth for companies, roles and periods; null = only the companies are checked.</param>
    /// <param name="header">The name, the contacts and the city the header prints: "Дополнительно" must not repeat them.</param>
    public static FaithfulnessReport Check(ResumeDocument doc, string sourceText, IReadOnlyCollection<string> vacancyKeywords, bool fix,
        IReadOnlyList<ProfileJob>? sourceJobs = null, IReadOnlyCollection<string>? header = null)
    {
        var contacts = (header ?? []).Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => TextUtil.Normalize(h)).ToHashSet(StringComparer.Ordinal);
        var numbers = SourceNumbers(sourceText);
        var sourceTech = TechForm(sourceText);
        var source = new TechThesaurus.Text(sourceText);
        var keywords = vacancyKeywords.Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
        var en = doc.Language == "en";
        var violations = new List<string>();
        var corrections = new List<string>();

        // Talk about gaps, the vacancy or the source is not a resume's: whole sentences and items go.
        string CleanText(string label, string text)
        {
            var kept = SplitSentences(text).Where(s => !GapTalk().IsMatch(TextUtil.Normalize(s))).ToList();
            if (kept.Count == SplitSentences(text).Count()) return text;
            violations.Add($"{label}: фраза о пробелах, вакансии или исходных данных — в резюме только то, что есть");
            corrections.Add($"из {label} убрана фраза о пробелах или вакансии");
            return string.Join(" ", kept);
        }

        // Companies: a job at a company the candidate never worked for is an invention, not a rephrasing.
        var jobs = new List<ResumeJob>();
        foreach (var job in doc.Experience)
        {
            if (!sourceTech.Contains(TechForm(job.Company), StringComparison.Ordinal))
            {
                violations.Add($"компании «{job.Company}» нет в резюме");
                corrections.Add($"убрано место работы «{job.Company}»");
                continue;
            }

            var truth = sourceJobs?.FirstOrDefault(j => SameCompany(j.Company, job.Company));
            var role = job.Role;
            var period = job.Period;
            if (truth is not null)
            {
                // The title and the dates are facts: a "Senior" the source does not have, or a moved date, is a claim.
                if (Letters(truth.Role) != Letters(job.Role))
                {
                    violations.Add($"должность в «{job.Company}» — как в резюме: «{truth.Role}», не «{job.Role}»");
                    corrections.Add($"должность в «{job.Company}» возвращена: «{truth.Role}»");
                    role = truth.Role;
                }

                var sourcePeriod = ResumePeriod.Parse(truth.Period);
                if (sourcePeriod is not null && ResumePeriod.Parse(job.Period) != sourcePeriod)
                {
                    violations.Add($"период в «{job.Company}» — как в резюме: «{ResumePeriod.Format(sourcePeriod, en)}»");
                    corrections.Add($"период в «{job.Company}» возвращён по резюме");
                    period = truth.Period;
                }
            }

            var bullets = new List<string>();
            foreach (var bullet in job.Bullets.Where(b => !string.IsNullOrWhiteSpace(b)))
            {
                var bad = UnverifiedNumbers(bullet, numbers);
                if (bad.Count > 0)
                {
                    violations.Add($"цифры {string.Join(", ", bad)} не из резюме: «{TextUtil.Truncate(bullet, 90)}»");
                    corrections.Add($"убран пункт с непроверенными цифрами ({string.Join(", ", bad)}): «{TextUtil.Truncate(bullet, 70)}»");
                    continue;
                }

                if (GapTalk().IsMatch(TextUtil.Normalize(bullet)))
                {
                    violations.Add($"пункт о пробелах или вакансии: «{TextUtil.Truncate(bullet, 90)}»");
                    corrections.Add($"убран пункт о пробелах или вакансии: «{TextUtil.Truncate(bullet, 70)}»");
                    continue;
                }

                if (bullet.Length > MaxBulletLength)
                    violations.Add($"пункт длиннее {MaxBulletLength} символов (одно предложение: действие, как, результат): «{TextUtil.Truncate(bullet, 70)}»");
                if (WeakStart().IsMatch(TextUtil.Normalize(bullet)))
                    violations.Add($"пункт начинается со слабого глагола: «{TextUtil.Truncate(bullet, 60)}» (что сделал и что это дало)");
                bullets.Add(bullet.Trim());
            }

            var stack = new List<string>();
            foreach (var item in job.Stack.Where(s => !string.IsNullOrWhiteSpace(s)).DistinctBy(TechForm))
            {
                if (TechThesaurus.Proven(item, source))
                {
                    stack.Add(item);
                    continue;
                }

                violations.Add($"в стеке «{job.Company}» «{item}» — такого нет в резюме");
                corrections.Add($"из стека «{job.Company}» убрано «{item}»");
            }

            var context = job.Context is null ? null : CleanText($"описание проекта «{job.Company}»", job.Context);
            jobs.Add(job with
            {
                Role = role, Period = ResumePeriod.Normalize(period, en), Bullets = bullets, Stack = stack,
                Context = string.IsNullOrWhiteSpace(context) ? null : context,
            });
        }

        // No job of the source may disappear: a hole in the dates is the first thing a recruiter asks about.
        foreach (var missed in sourceJobs?.Where(j => !jobs.Any(x => SameCompany(j.Company, x.Company))) ?? [])
        {
            violations.Add($"пропущено место работы «{missed.Company}» — в резюме все места работы");
            corrections.Add($"возвращено место работы «{missed.Company}» с пунктами из профиля");
            jobs.Add(new ResumeJob
            {
                Company = missed.Company, Role = missed.Role, Period = ResumePeriod.Normalize(missed.Period, en),
                Bullets = SplitSentences(missed.Highlights).Take(3).ToList(),
            });
        }

        var ordered = jobs.OrderBy(j => ResumePeriod.Parse(j.Period), ResumePeriod.Recent).ToList();
        if (!ordered.SequenceEqual(jobs))
        {
            violations.Add("места работы — от последнего к первому");
            corrections.Add("места работы упорядочены от последнего к первому");
        }

        var summary = CleanText("summary", doc.Summary);
        var headline = doc.Headline;
        foreach (var (label, text) in new[] { ("summary", summary), ("headline", headline) })
        {
            var bad = UnverifiedNumbers(text, numbers);
            if (bad.Count == 0) continue;
            violations.Add($"{label}: цифры {string.Join(", ", bad)} не из резюме");
            if (label == "summary")
            {
                summary = string.Join(" ", SplitSentences(summary).Where(s => UnverifiedNumbers(s, numbers).Count == 0));
                corrections.Add("из summary убраны предложения с непроверенными цифрами");
            }
            else
            {
                headline = NumberToken().Replace(headline, "").Trim();
            }
        }

        if (summary.Length > MaxSummaryLength || SplitSentences(summary).Count() > MaxSummarySentences)
            violations.Add($"summary: не больше {MaxSummarySentences} предложений и {MaxSummaryLength} символов — кто, чем силён под эту вакансию, два доказательства");

        // Skills: listed only if the source has them or they follow from it; otherwise "familiar" when the vacancy wants it, otherwise dropped.
        var familiar = new List<string>();
        var groups = new List<SkillGroup>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in doc.Skills)
        {
            var items = new List<string>();
            foreach (var item in group.Items.Where(i => !string.IsNullOrWhiteSpace(i)))
            {
                if (!seen.Add(TechForm(item)))
                {
                    corrections.Add($"повтор навыка «{item}» убран");
                    continue;
                }

                if (TechThesaurus.Proven(item, source) || Known(item, sourceTech, source, doc.Language))
                {
                    items.Add(item);
                    continue;
                }

                violations.Add($"навыка «{item}» нет в резюме");
                if (InKeywords(item, keywords))
                {
                    familiar.Add(item);
                    corrections.Add($"«{item}» перенесён в «Также знаком»");
                }
                else
                {
                    corrections.Add($"убран навык «{item}»");
                }
            }

            if (items.Count > 0) groups.Add(group with { Items = items });
        }

        // "Familiar" is for what the candidate has not used: what the source proves belongs to the skills, under its group.
        foreach (var item in doc.Familiar.Where(f => !string.IsNullOrWhiteSpace(f)))
        {
            if (seen.Contains(TechForm(item))) continue;
            if (TechThesaurus.Proven(item, source))
            {
                violations.Add($"«{item}» есть в опыте — это навык, не «Также знаком»");
                var home = groups.FirstOrDefault(g => g.Items.Any(i => SameFamily(i, item))) ?? groups.FirstOrDefault();
                if (home is not null)
                {
                    groups[groups.IndexOf(home)] = home with { Items = [.. home.Items, item] };
                    seen.Add(TechForm(item));
                    corrections.Add($"«{item}» перенесён из «Также знаком» в навыки");
                }

                continue;
            }

            familiar.Add(item);
        }

        var familiarFinal = familiar.Where(f => InKeywords(f, keywords)).DistinctBy(TechForm).Take(MaxFamiliar).ToList();
        foreach (var dropped in familiar.Except(familiarFinal))
            corrections.Add($"из «Также знаком» убрано «{dropped}» ({(InKeywords(dropped, keywords) ? "больше трёх" : "нет в вакансии")})");

        var extras = doc.Extras.Where(x => !GapTalk().IsMatch(TextUtil.Normalize(x))).ToList();
        if (extras.Count < doc.Extras.Count)
        {
            violations.Add("«Дополнительно»: пункт о пробелах или вакансии");
            corrections.Add("из «Дополнительно» убран пункт о пробелах или вакансии");
        }

        // The header has the name, the contacts and the city: "Дополнительно" repeating them is a page of noise.
        // Inside a line only the name and the contacts count ("Удалённо из Минска…" is logistics, not the header again); the city alone does.
        var repeated = extras.Where(x => contacts.Any(c => c.Length >= 8 && TextUtil.Normalize(x).Contains(c, StringComparison.Ordinal))
                                         || contacts.Contains(TextUtil.Normalize(x).Trim(' ', '.'))).ToList();
        if (repeated.Count > 0)
        {
            violations.Add("«Дополнительно» повторяет имя, контакты или город из шапки");
            corrections.Add($"из «Дополнительно» убрано повторение шапки: {string.Join("; ", repeated.Select(r => TextUtil.Truncate(r, 40)))}");
            extras = extras.Except(repeated).ToList();
        }

        // Money is for the conversation: a floor printed in the resume anchors every offer at it (and the letters do not name it either).
        var salary = extras.Where(x => Salary().IsMatch(x)).ToList();
        if (salary.Count > 0)
        {
            violations.Add("зарплатные ожидания в резюме не пишутся: о деньгах — в разговоре");
            corrections.Add($"из «Дополнительно» убраны зарплатные ожидания: {string.Join("; ", salary.Select(x => TextUtil.Truncate(x, 50)))}");
            extras = extras.Except(salary).ToList();
        }

        // A condition about oneself reads as a problem: the clause goes, what fits stays.
        for (var i = 0; i < extras.Count; i++)
        {
            if (Condition().Match(extras[i]) is not { Success: true } condition) continue;
            violations.Add($"условие вместо утверждения: «{condition.Value.Trim(' ', ',')}» — напиши, что подходит");
            corrections.Add($"из «Дополнительно» убрано условие «{condition.Value.Trim(' ', ',')}»");
            extras[i] = Condition().Replace(extras[i], "").Trim();
        }

        // A result the summary names is a result of a job: a reader who looks for it in the experience must find it there too.
        var bulletNumbers = ordered.SelectMany(j => j.Bullets.Concat(j.Context is null ? [] : [j.Context])).SelectMany(Numbers).ToHashSet(StringComparer.Ordinal);
        var orphans = Numbers(Years().Replace(summary, "")).Where(n => n.Length > 1 && !bulletNumbers.Contains(n)).Distinct().ToList();
        if (orphans.Count > 0)
            violations.Add($"summary называет результаты, которых нет в опыте ({string.Join(", ", orphans)}): такой пункт должен быть и в своём месте работы");

        // The years of experience are not a result: a summary needs at least one number beyond them.
        if (!string.IsNullOrWhiteSpace(summary) && !Numbers(Years().Replace(summary, "")).Any())
            violations.Add("summary без доказательств: два самых сильных результата под вакансию — с цифрами из резюме");

        var fixedDoc = fix
            ? doc with
            {
                Experience = ordered, Summary = summary, Headline = headline, Skills = groups, Familiar = familiarFinal, Extras = extras,
            }
            : doc;
        var (covered, unshown, missing) = Coverage(fixedDoc, keywords, source);
        if (unshown.Count > 0)
            violations.Add($"это есть в опыте, но в тексте нет словами вакансии — напиши дословно там, где это правда: {string.Join(", ", unshown)}");
        return new FaithfulnessReport(fixedDoc, violations, fix ? corrections : [], covered, missing, unshown);
    }

    /// <summary>Vacancy keywords present in the final document (the ATS view), the proven ones it does not show, and the gaps.</summary>
    public static (List<string> Covered, List<string> Unshown, List<string> Missing) Coverage(ResumeDocument doc, IReadOnlyCollection<string> keywords,
        TechThesaurus.Text source)
    {
        var text = new TechThesaurus.Text(string.Join(" \n ", AllText(doc)));
        var covered = new List<string>();
        var unshown = new List<string>();
        var missing = new List<string>();
        foreach (var k in keywords.DistinctBy(TechForm))
            (TechThesaurus.Visible(k, text) ? covered : TechThesaurus.Proven(k, source) ? unshown : missing).Add(k);
        return (covered, unshown, missing);
    }

    /// <summary>Vacancy keywords present in the final document (the ATS view).</summary>
    public static (List<string> Covered, List<string> Missing) Coverage(ResumeDocument doc, IReadOnlyCollection<string> keywords)
    {
        var text = new TechThesaurus.Text(string.Join(" \n ", AllText(doc)));
        var covered = new List<string>();
        var missing = new List<string>();
        foreach (var k in keywords.DistinctBy(TechForm)) (TechThesaurus.Visible(k, text) ? covered : missing).Add(k);
        return (covered, missing);
    }

    public static IEnumerable<string> AllText(ResumeDocument doc)
    {
        yield return doc.Headline;
        yield return doc.Location;
        yield return doc.Summary;
        foreach (var g in doc.Skills)
        {
            yield return g.Group;
            foreach (var i in g.Items) yield return i;
        }

        foreach (var f in doc.Familiar) yield return f;
        foreach (var j in doc.Experience)
        {
            yield return j.Role;
            yield return j.Company;
            if (j.Context is not null) yield return j.Context;
            foreach (var b in j.Bullets) yield return b;
            foreach (var s in j.Stack) yield return s;
        }

        foreach (var e in doc.Extras) yield return e;
    }

    /// <summary>Numbers of <paramref name="text"/> that the source does not contain (as is, or as the integer part of a source decimal).</summary>
    public static List<string> UnverifiedNumbers(string text, (HashSet<string> Exact, HashSet<string> IntegerParts) source) =>
        Numbers(text).Where(n => !source.Exact.Contains(n) && !source.IntegerParts.Contains(n)).Distinct().ToList();

    public static (HashSet<string> Exact, HashSet<string> IntegerParts) SourceNumbers(string text)
    {
        var exact = Numbers(text).ToHashSet(StringComparer.Ordinal);
        var ints = exact.Select(n => n.Split('.')[0]).ToHashSet(StringComparer.Ordinal);
        return (exact, ints);
    }

    /// <summary>Standalone numbers, normalized: "5 000" → "5000", "99,99" → "99.99". Parts of names (p99, S3, .NET 8 stays "8") count too.</summary>
    public static IEnumerable<string> Numbers(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        var joined = DigitGroupSpace().Replace(text, "");
        foreach (Match m in NumberToken().Matches(joined))
        {
            var n = m.Value.Replace(',', '.');
            if (decimal.TryParse(n, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
                yield return d.ToString(d == Math.Floor(d) ? "0" : "0.##########", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Comparison form of technology names: lower case, no spaces ("ASP.NET Core" ≈ "asp.netcore").</summary>
    public static string TechForm(string? s) => Spaces().Replace(TextUtil.Normalize(s), "");

    /// <summary>Talk that has no place in a resume: gaps, the vacancy, the source it was made from, promises to learn.</summary>
    public static bool IsGapTalk(string text) => GapTalk().IsMatch(TextUtil.Normalize(text));

    private static bool SameCompany(string a, string b)
    {
        var (x, y) = (TechForm(a), TechForm(b));
        return x.Length > 0 && y.Length > 0 && (x.Contains(y, StringComparison.Ordinal) || y.Contains(x, StringComparison.Ordinal));
    }

    /// <summary>Letters and digits only, lower case: "C#/.NET-разработчик" ≈ "C#/.NET разработчик".</summary>
    private static string Letters(string s) => string.Concat(TextUtil.Normalize(s).Where(c => char.IsLetterOrDigit(c) || c == '#'));

    /// <summary>Two skills of one family (a relabeled "T-SQL" goes next to "MS SQL Server").</summary>
    private static bool SameFamily(string a, string b)
    {
        var names = TechThesaurus.Names(b).Select(TechForm).ToHashSet(StringComparer.Ordinal);
        return TechThesaurus.Names(a).Any(n => names.Contains(TechForm(n)))
               || (TechForm(a).Contains("sql", StringComparison.Ordinal) && TechForm(b).Contains("sql", StringComparison.Ordinal));
    }

    private static bool Known(string item, string sourceTech, TechThesaurus.Text source, string language)
    {
        var tech = TechForm(item);
        if (tech.Length == 0) return true;
        if (sourceTech.Contains(tech, StringComparison.Ordinal)) return true;
        // A translated practice ("High-load systems" for "высоконагруженные системы") cannot be matched word by word: in an English
        // resume only technology names (capitals, digits, #, +, dots) are checked.
        if (!TechLike().IsMatch(item)) return language == "en" || Stems.Covers(source.Stems, item);
        return false;
    }

    private static bool InKeywords(string item, IReadOnlyCollection<string> keywords)
    {
        var tech = TechForm(item);
        return keywords.Any(k =>
        {
            var kt = TechForm(k);
            return kt.Length > 0 && (kt.Contains(tech, StringComparison.Ordinal) || tech.Contains(kt, StringComparison.Ordinal) || TechThesaurus.SameThing(k, item));
        });
    }

    private static IEnumerable<string> SplitSentences(string text) =>
        Sentence().Split(text ?? "").Select(s => s.Trim()).Where(s => s.Length > 0);

    /// <summary>
    /// Gaps («в предоставленном опыте не указаны», «опыта с X нет», «готов изучить»), the vacancy and the candidate named in the third
    /// person, the resume talking about itself — in Russian and English (normalized text: lower case, ё → е).
    /// </summary>
    [GeneratedRegex(@"(в|из|по) (предоставленн|представленн|указанн|имеющ|приведенн)\p{L}* (опыт|резюме|данн|материал|информац|сведени)\p{L}*|(?<!\p{L})не (указан|упомина|отраж|подтвержд|представлен)\p{L}*|(нет|отсутству\p{L}*|не имею|не имеет) (коммерческ\p{L}* |прямого |практическ\p{L}* )?(опыт|данн|информац|сведени)\p{L}*|опыт\p{L}* [^.]{0,60}(не было|отсутству\p{L}*)|(?<!\p{L})ваканси\p{L}*|(?<!\p{L})кандидат\p{L}*|(?<!\p{L})резюме(?!\p{L})|готов (изучить|освоить|разобраться|погрузиться)|быстро (обучаюсь|осваива\p{L}*)|not (mentioned|specified|listed|stated|provided)|no (direct |commercial |hands-on )?experience|(?<!\p{L})provided (resume|experience|information)|the candidate|job description|willing to learn")]
    private static partial Regex GapTalk();

    /// <summary>The candidate's own condition: ", при подходящем варианте оформления", "если это возможно".</summary>
    [GeneratedRegex(@",?\s*(при (подходящ|соответствующ|определ[её]нн|устраивающ)\p{L}* (вариант|услови|формат|оформлени)\p{L}*( оформлени\p{L}*)?|если (это )?(возможно|получится|подойд[её]т))", RegexOptions.IgnoreCase)]
    private static partial Regex Condition();

    [GeneratedRegex(@"зарплат\p{L}*|заработн\p{L}* плат\p{L}*|ожидани\p{L}* по (оплат|доход|зп)\p{L}*|(?<!\p{L})зп(?!\p{L})|salary|compensation|\d[\d\s]*\s?(usd|eur|byn|руб\p{L}*|\$|€)", RegexOptions.IgnoreCase)]
    private static partial Regex Salary();

    [GeneratedRegex(@"\d+\+?\s*(лет|года|годами|год|years?)(?!\p{L})", RegexOptions.IgnoreCase)]
    private static partial Regex Years();

    /// <summary>A bullet that says the candidate was near something instead of what they did.</summary>
    [GeneratedRegex(@"^(работал\p{L}*|участвовал\p{L}*|занимал\p{L}*|помогал\p{L}*|был\p{L}*|использовал\p{L}*|worked|was|helped|participated|involved)(?!\p{L})")]
    private static partial Regex WeakStart();

    [GeneratedRegex(@"[A-Z0-9#+.]")]
    private static partial Regex TechLike();

    [GeneratedRegex(@"(?<=\d)[\s  ](?=\d{3}(?!\d))")]
    private static partial Regex DigitGroupSpace();

    [GeneratedRegex(@"(?<![\p{L}\d.,])\d+(?:[.,]\d+)?")]
    private static partial Regex NumberToken();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex Sentence();
}
