using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;

namespace JobFinder.Pipeline.Modules.Hiring;

internal sealed class LetterContent(TimeProvider time, ILogger log)
{
    public async Task<LetterCandidate> DraftAsync(JobFinderLlm op, CandidateProfile profile, CandidateFacts sheet, LetterBrief brief,
        DateTimeOffset now, string? remarks, CancellationToken ct)
    {
        var vacancy = brief.Vacancy;
        using var stage = JobFinder.Core.Telemetry.Telemetry.StartStage("draft", "letter").Tag("vacancy.id", vacancy.Id);
        var factsText = sheet.ForPrompt();
        var evidence = ProfileEvidence.Text(profile, sheet);
        var maxMetrics = brief.Channel == LetterChannel.Telegram ? RecruiterStyle.MaxMetrics : 1;
        var reference = Reference(brief) is { } line ? HumanText.Clean(line) : null;
        if (brief.Channel == LetterChannel.LinkedIn && reference?.Length >= LetterLayout.LinkedInLimit)
            return new LetterCandidate { Text = reference, Written = "", Problems = ["строка о вакансии уже занимает весь лимит LinkedIn; текст не усекался"] };
        var signature = brief.Channel == LetterChannel.Email ? LetterWriter.FirstName(profile.Name) : null;
        var result = await op.RecruiterMessageAsync(profile, vacancy, factsText, brief.Contact, brief.Situation, ChannelText(brief.Channel, reference),
            brief.AttachesResume, brief.Conversation, now, brief.First, ct, remarks);
        var parts = result.Value;
        var written = HumanText.Clean(LetterLayout.Compose(parts.Greeting, null, parts.Paragraphs, parts.Closing));
        var text = HumanText.Clean(LetterLayout.Compose(parts.Greeting, reference, parts.Paragraphs, parts.Closing))
                   + (signature is null ? "" : "\n\n" + signature);
        var problems = RecruiterStyle.Problems(written, brief.First, maxMetrics, brief.Channel == LetterChannel.LinkedIn ? 0 : RecruiterStyle.MinFirstLength);
        problems.AddRange(RecruiterStyle.LayoutProblems(parts.Greeting, parts.Paragraphs, parts.Closing, brief.First));
        var limit = Limit(brief.Channel);
        if (text.Length > limit)
            problems.Add($"вместе со строкой о вакансии длиннее {limit} символов: сократи абзацы");
        var picked = parts.Facts.Select(fact => new RecruiterEvidence(fact.Fact, fact.SourceQuote, ProfileEvidence.Contains(evidence, fact.SourceQuote))).ToList();
        problems.AddRange(picked.Where(proof => !proof.Found).Select(proof => $"факт без опоры в профиле (цитаты нет): «{TextUtil.Truncate(proof.Fact, 90)}»"));
        if (brief.NeedsFact && picked.Count(proof => proof.Found) == 0) problems.Add("нет ни одного подтверждённого факта из опыта под вакансию");
        return new LetterCandidate { Text = text, Written = written, Evidence = picked, Problems = problems, Score = 0 };
    }

    public async Task<LetterCandidate> ReviewAsync(JobFinderLlm op, CandidateProfile profile, CandidateFacts sheet, LetterBrief brief,
        LetterCandidate candidate, CancellationToken ct)
    {
        using var stage = JobFinder.Core.Telemetry.Telemetry.StartStage("review", "letter").Tag("vacancy.id", brief.Vacancy.Id);
        var problems = candidate.Problems.ToList();
        var score = 0;
        if (problems.Count == 0)
        {
            var check = await CheckAsync(op, profile, sheet.ForPrompt(), ProfileEvidence.Text(profile, sheet), brief.Vacancy, candidate.Text, brief, ct);
            problems.AddRange(check.Problems);
            score = brief.First ? check.Score : problems.Count == 0 ? 10 : 0;
        }

        return new LetterCandidate
        {
            Text = candidate.Text, Written = candidate.Written, Evidence = candidate.Evidence.ToList(), Problems = problems, Score = score,
            Review = Report(candidate.Text, LetterWriter.ContextRecipe(op, profile, sheet, brief), score, problems),
        };
    }

    public async Task<ApplicationLetterReview> ReviewTextAsync(JobFinderLlm op, CandidateProfile profile, CandidateFacts sheet, LetterBrief brief,
        string text, CancellationToken ct)
    {
        var vacancy = brief.Vacancy;
        var recipe = LetterWriter.ContextRecipe(op, profile, sheet, brief);
        var contextHash = recipe.Hash;
        if (LetterWriter.Matches(vacancy.LetterReview, text, contextHash)) return vacancy.LetterReview!;
        using var stage = JobFinder.Core.Telemetry.Telemetry.StartStage("review", "letter").Tag("vacancy.id", vacancy.Id);
        var reference = Reference(brief) is { } line ? HumanText.Clean(line) : null;
        var written = reference is null ? text : text.Replace(reference, "", StringComparison.Ordinal);
        var blocks = written.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (brief.Channel == LetterChannel.Email && blocks.LastOrDefault() == LetterWriter.FirstName(profile.Name)) blocks.RemoveAt(blocks.Count - 1);
        var problems = RecruiterStyle.Problems(string.Join("\n\n", blocks), brief.First, brief.Channel == LetterChannel.Telegram ? RecruiterStyle.MaxMetrics : 1,
            brief.Channel == LetterChannel.LinkedIn ? 0 : RecruiterStyle.MinFirstLength);
        if (brief.First)
            problems.AddRange(RecruiterStyle.LayoutProblems(blocks.FirstOrDefault() ?? "", blocks.Skip(1).SkipLast(1).ToList(), blocks.Count > 1 ? blocks[^1] : "", first: true));
        if (reference is not null && !text.Split('\n')[0].Contains(reference, StringComparison.Ordinal)) problems.Add("первая строка не называет вакансию и её ссылку");
        if (text.Length > Limit(brief.Channel)) problems.Add($"письмо длиннее {Limit(brief.Channel)} символов");
        var score = 0;
        if (problems.Count == 0)
        {
            var check = await CheckAsync(op, profile, sheet.ForPrompt(), ProfileEvidence.Text(profile, sheet), vacancy, text, brief, ct);
            problems.AddRange(check.Problems);
            score = brief.First ? check.Score : problems.Count == 0 ? 10 : 0;
        }

        return Report(text, recipe, score, problems);
    }

    private ApplicationLetterReview Report(string text, Core.Artifacts.ArtifactRecipe recipe, int score, IReadOnlyList<string> problems) => new()
    {
        TextHash = Fingerprint.Of(text), ContextHash = recipe.Hash, Recipe = recipe, Score = score, Problems = problems.ToList(), CheckedAt = time.GetUtcNow(),
    };

    private static string? Reference(LetterBrief brief) => !brief.First || brief.Channel == LetterChannel.Hh ? null
        : brief.Channel == LetterChannel.LinkedIn ? LetterLayout.LinkedInReference(brief.Vacancy) : LetterLayout.VacancyReference(brief.Vacancy);

    private static int Limit(LetterChannel channel) => channel == LetterChannel.LinkedIn ? LetterLayout.LinkedInLimit : RecruiterStyle.MaxComposedLength;

    private static string ChannelText(LetterChannel channel, string? reference) => channel switch
    {
        LetterChannel.LinkedIn =>
            $"LinkedIn: заметка к запросу в контакты. Жёсткий лимит всего сообщения {LetterLayout.LinkedInLimit} символов, включая пробелы и переносы. " +
            $"Код добавит строку «{reference}»; на greeting, один короткий paragraph и closing вместе с разделителями остаётся " +
            $"{Math.Max(0, LetterLayout.LinkedInLimit - (reference?.Length ?? 0) - 5)} символов. Это исключение из обычной длины первого сообщения. " +
            "Один подтверждённый кейс с результатом по главной задаче вакансии и короткий следующий шаг. Без вложений, подписи и повторения вакансии.",
        LetterChannel.Hh =>
            "Сопроводительное письмо к отклику на hh.ru / rabota.by. Его читает рекрутер или HR в карточке отклика рядом с резюме, какая это " +
            "вакансия, он видит и так: строки о вакансии не будет, вложений и контактов не упоминай. Имя неизвестно: greeting «Здравствуйте!». " +
            "Без подписи: имя стоит в отклике.",
        LetterChannel.Email =>
            "Письмо на почту рекрутеру (тема письма с названием вакансии отдельно). Код поставит первой строку о вакансии со ссылкой и в " +
            "конце подпись именем: ни того, ни другого не пиши.",
        _ => "Личное сообщение в Telegram: как пишет живой человек в мессенджере, короткими абзацами, без подписи. Код поставит первой строку " +
             "о вакансии со ссылкой на неё.",
    };

    private async Task<(List<string> Problems, int Score)> CheckAsync(JobFinderLlm op, CandidateProfile profile, string factsText, string evidence,
        Vacancy vacancy, string text, LetterBrief brief, CancellationToken ct)
    {
        MessageCheck check;
        try
        {
            check = (await op.CheckRecruiterMessageAsync(profile, factsText, vacancy, text, brief.Deep, ct, first: brief.First)).Value;
        }
        catch (JobFinder.Llm.Client.LlmUnavailableException error)
        {
            log.Warn($"[letter] verification unavailable, sending blocked: {error.Message}");
            throw;
        }

        var problems = check.Claims.Where(claim => !claim.Supported || !ProfileEvidence.Contains(evidence, claim.Quote, claim.Kind == "logistics" ? 1 : 3))
            .Select(claim => $"утверждение без опоры в профиле: «{TextUtil.Truncate(claim.Claim, 100)}» " +
                         (claim.Supported ? $"(цитата «{TextUtil.Truncate(claim.Quote, 60)}» не найдена в профиле дословно: скажи словами профиля или убери)"
                                      : "(убери или замени подтверждённым)")).ToList();
        if (brief.NeedsFact && !check.Claims.Any(claim => claim.Kind == "experience" && claim.Supported && ProfileEvidence.Contains(evidence, claim.Quote)))
            problems.Add("проверка не подтвердила ни одного утверждения об опыте цитатой из профиля");
        if (brief.First && !check.Verdict.WouldReply) problems.Add("рекрутер не ответил бы на это письмо: " + check.Verdict.Weakest);
        if (check.ProjectCases > 2) problems.Add($"описано {check.ProjectCases} проекта: оставь один кейс и максимум одну короткую подкрепляющую фразу");
        if (brief.First && !check.HasReason)
            problems.Add("нет конкретной причины писать именно в эту вакансию: начни с того, что в ней зацепило (задача, домен, сочетание)");
        if (!check.Natural) problems.Add("без должности и технологий звучит как шаблон «я подхожу под вакансию»" + (check.Note.Length > 0 ? ": " + check.Note : ""));
        if (!brief.First) return (problems, 0);

        var verdict = check.Verdict.Scored();
        var failed = verdict.Checks.Where(criterion => !criterion.Passed).ToList();
        if (verdict.Score < LetterWriter.MinVerdict)
            problems.Add($"рекрутер: {verdict.Score}/10 — не выполнено: " +
                         string.Join("; ", failed.Select(criterion => $"{RecruiterVerdict.Criteria.GetValueOrDefault(criterion.Id).Question ?? criterion.Id} ({criterion.Note})")) +
                         (verdict.Fix.Length > 0 ? $"; исправь: {verdict.Fix}" : ""));
        log.Info($"[letter] #{vacancy.Id} {brief.Channel}: recruiter checklist {verdict.Score}/10" +
                 (failed.Count > 0 ? $" (не выполнено: {string.Join(", ", failed.Select(criterion => criterion.Id))})" : ""));
        return (problems, verdict.Score);
    }
}
