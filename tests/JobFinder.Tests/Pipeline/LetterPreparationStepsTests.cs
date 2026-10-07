using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Llm;
using JobFinder.Llm.Client;
using JobFinder.Llm.Prompts;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Modules.Hiring;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobFinder.Tests.Pipeline;

public sealed class LetterPreparationStepsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Quote = "Устранил риск двойных списаний и отрицательного баланса при конкурентных ставках.";

    private static async Task<(LetterPreparationSteps Steps, LetterPreparationInput Input, LlmOptions Options)> Setup(Http http, bool first = true,
        LetterChannel channel = LetterChannel.Telegram)
    {
        var options = Llm.LlmRuntimePolicyTests.Bootstrap();
        options.MaxAttempts = 1;
        options.Routes[LlmPurpose.ReplyDeep] = [new LlmModelOptions { Name = "deep-reply", Reasoning = new() { Effort = "xhigh" } }];
        options.Routes[LlmPurpose.Reply] = [new LlmModelOptions { Name = "reply" }];
        var client = new LlmClient(new OpenRouterTransport(http, options), new ModelHealth(TimeProvider.System), options, new Usage(), NullLogger<LlmClient>.Instance);
        var llm = new JobFinderLlm(client, new PromptCatalog(Fixture.PromptsDir));
        var operation = await llm.ForOperationAsync(Ct);
        var profile = new CandidateProfile { Name = "Иван Иванов", Summary = Quote, Reviewed = true };
        var vacancy = new Vacancy { Id = 123, Title = "Senior .NET", CompanyName = "Synthetic Payments", Description = "Платёжная платформа: надёжность конкурентных операций.", Url = "https://example.invalid/vacancy/123", Fingerprint = "synthetic" };
        var brief = new LetterBrief(vacancy, channel, "Иван", "Отклик на платёжную платформу.", channel != LetterChannel.LinkedIn, "(нет переписки)", first, true, true);
        var input = new LetterPreparationInput
        {
            Brief = LetterBriefInput.From(brief), Anchor = AssessmentVacancy.From(vacancy), Profile = profile, Facts = new(), ProfileVersion = 7, FactsVersion = 4,
            CapturedAt = new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero), Operation = operation.CaptureOperation(),
        };
        input.Recipe = LetterWriter.ContextRecipe(operation, profile, input.Facts, brief);
        return (new LetterPreparationSteps(llm, TimeProvider.System, NullLogger<LetterPreparationSteps>.Instance), input, options);
    }

    [Fact]
    public async Task Draft_and_review_are_independent_maximum_effort_calls_with_checkpoint_roundtrip()
    {
        using var http = new Http();
        var (steps, input, options) = await Setup(http);
        options.Routes[LlmPurpose.Application][0].Name = "changed-bootstrap-model";
        var draft = await steps.DraftAsync(input, null, Ct);
        Assert.Empty(draft.Problems);
        Assert.Null(draft.Review);
        Assert.Single(http.Requests);
        var saved = LetterPreparationRequests.ReadCandidate(LetterPreparationRequests.SerializeCandidate(draft));
        var review = await steps.ReviewAsync(input, saved, Ct);
        Assert.Empty(review.Problems);
        Assert.Equal(10, review.Score);
        Assert.True(LetterPreparationWorkflow.Ready(review));
        Assert.Equal(input.Recipe.Hash, review.Review!.ContextHash);
        Assert.Contains("Пишу по вакансии", review.Text.Split('\n')[0]);
        Assert.Contains("Synthetic Payments", review.Text.Split('\n')[0]);
        Assert.Equal(2, http.Requests.Count);
        Assert.All(http.Requests, request =>
        {
            Assert.Equal("shared-model", request["model"]!.GetValue<string>());
            Assert.Equal("xhigh", request["reasoning"]!["effort"]!.GetValue<string>());
        });
    }

    [Theory]
    [InlineData(300, true)]
    [InlineData(301, false)]
    public async Task LinkedIn_enforces_total_length_before_paid_review_without_truncating_the_text(int length, bool ready)
    {
        using var http = new Http();
        var (steps, input, _) = await Setup(http, channel: LetterChannel.LinkedIn);
        var reference = JobFinder.Core.Text.LetterLayout.LinkedInReference(input.Brief.ToBrief().Vacancy);
        http.Message.Paragraphs = [Quote];
        http.Message.Closing = "Обсудим задачи?";
        var composed = JobFinder.Core.Text.LetterLayout.Compose(http.Message.Greeting, reference, http.Message.Paragraphs, http.Message.Closing);
        http.Message.Paragraphs[0] += " " + new string('а', length - composed.Length - 1);
        var draft = await steps.DraftAsync(input, null, Ct);
        Assert.Equal(length, draft.Text.Length);
        Assert.Contains(input.Brief.Vacancy.Url, draft.Text);
        var review = await steps.ReviewAsync(input, LetterPreparationRequests.ReadCandidate(LetterPreparationRequests.SerializeCandidate(draft)), Ct);
        Assert.Equal(draft.Text, review.Text);
        Assert.Equal(ready, LetterPreparationWorkflow.Ready(review));
        Assert.Equal(ready ? 2 : 1, http.Requests.Count);
        Assert.All(http.Requests, request => Assert.Equal("xhigh", request["reasoning"]!["effort"]!.GetValue<string>()));
    }

    [Fact]
    public async Task LinkedIn_short_note_keeps_the_nine_point_checklist_and_grounded_case()
    {
        using var http = new Http();
        var (steps, input, _) = await Setup(http, channel: LetterChannel.LinkedIn);
        http.Message.Paragraphs = [Quote];
        http.Message.Closing = "Обсудим задачи?";
        var draft = await steps.DraftAsync(input, null, Ct);
        Assert.Empty(draft.Problems);
        Assert.True(draft.Written.Length < JobFinder.Core.Filtering.RecruiterStyle.MinFirstLength);
        http.Check.Verdict.Checks.Single(check => check.Id == "result").Passed = false;
        var review = await steps.ReviewAsync(input, draft, Ct);
        Assert.Equal(8, review.Score);
        Assert.False(LetterPreparationWorkflow.Ready(review));
        Assert.Contains(review.Problems, problem => problem.Contains("8/10", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_impossible_LinkedIn_reference_is_rejected_without_any_paid_call_or_url_truncation()
    {
        using var http = new Http();
        var (steps, input, _) = await Setup(http, channel: LetterChannel.LinkedIn);
        input.Brief.Vacancy.Url = "https://example.invalid/" + new string('x', 400);
        var options = Llm.LlmRuntimePolicyTests.Bootstrap();
        var operation = new JobFinderLlm(new LlmClient(new OpenRouterTransport(http, options), new ModelHealth(TimeProvider.System),
            options, new Usage(), NullLogger<LlmClient>.Instance), new PromptCatalog(Fixture.PromptsDir)).RestoreOperation(input.Operation);
        input.Recipe = LetterWriter.ContextRecipe(operation, input.Profile, input.Facts, input.Brief.ToBrief());
        var draft = await steps.DraftAsync(input, null, Ct);
        var review = await steps.ReviewAsync(input, draft, Ct);
        Assert.NotEmpty(review.Problems);
        Assert.False(LetterPreparationWorkflow.Ready(review));
        Assert.Contains(input.Brief.Vacancy.Url, review.Text);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Exact_LinkedIn_review_rejects_oversize_text_before_calling_the_provider()
    {
        using var http = new Http();
        var (steps, input, _) = await Setup(http, channel: LetterChannel.LinkedIn);
        var options = Llm.LlmRuntimePolicyTests.Bootstrap();
        var operation = new JobFinderLlm(new LlmClient(new OpenRouterTransport(http, options), new ModelHealth(TimeProvider.System),
            options, new Usage(), NullLogger<LlmClient>.Instance), new PromptCatalog(Fixture.PromptsDir)).RestoreOperation(input.Operation);
        input.Version = 2;
        input.ExactText = JobFinder.Core.Text.LetterLayout.Compose("Здравствуйте!", JobFinder.Core.Text.LetterLayout.LinkedInReference(input.Brief.ToBrief().Vacancy),
            [Quote + " " + new string('а', 110)], "Обсудим задачи?");
        Assert.True(input.ExactText.Length > JobFinder.Core.Text.LetterLayout.LinkedInLimit);
        input.ReviewContextHash = input.Recipe.Hash;
        input.Recipe = LetterWriter.ReviewRecipe(operation, input.Profile, input.Facts, input.Brief.ToBrief(), input.ExactText);
        var result = await steps.ReviewAsync(input, new LetterCandidate { Text = input.ExactText, Written = input.ExactText }, Ct);
        Assert.Equal(input.ExactText, result.Text);
        Assert.False(LetterPreparationWorkflow.Ready(result));
        Assert.Contains(result.Problems, problem => problem.Contains("300", StringComparison.Ordinal));
        Assert.Empty(http.Requests);
    }

    [Theory]
    [InlineData("proof")]
    [InlineData("layout")]
    public async Task Cheap_rejections_are_checkpointable_and_do_not_pay_for_a_review(string reason)
    {
        using var http = new Http();
        if (reason == "proof") http.Message.Facts[0].SourceQuote = "Invented project not in the captured profile.";
        else http.Message.Greeting = "Откликаюсь на вашу вакансию.";
        var (steps, input, _) = await Setup(http);
        var draft = await steps.DraftAsync(input, null, Ct);
        Assert.NotEmpty(draft.Problems);
        var review = await steps.ReviewAsync(input, LetterPreparationRequests.ReadCandidate(LetterPreparationRequests.SerializeCandidate(draft)), Ct);
        Assert.False(LetterPreparationWorkflow.Ready(review));
        Assert.NotEmpty(review.Problems);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Unsupported_claim_is_blocked_even_when_the_recruiter_checklist_is_perfect()
    {
        using var http = new Http();
        http.Check.Claims[0].Supported = false;
        var (steps, input, _) = await Setup(http);
        var draft = await steps.DraftAsync(input, null, Ct);
        var review = await steps.ReviewAsync(input, draft, Ct);
        Assert.Equal(10, review.Score);
        Assert.NotEmpty(review.Problems);
        Assert.False(LetterPreparationWorkflow.Ready(review));
    }

    [Fact]
    public async Task Captured_date_and_best_previous_text_are_the_actual_writer_inputs()
    {
        using var http = new Http();
        var (steps, input, _) = await Setup(http);
        await steps.DraftAsync(input, new LetterCandidate { Written = "Previous best text", Problems = ["Preserve the strongest real story"] }, Ct);
        var messages = http.Requests.Single()["messages"]!.ToJsonString();
        Assert.Contains("2026", messages);
        Assert.Contains("Previous best text", messages);
        Assert.Contains("Preserve the strongest real story", messages);
    }

    [Fact]
    public async Task Clean_nonfirst_review_is_ten_and_keeps_its_reply_route()
    {
        using var http = new Http();
        var (steps, input, _) = await Setup(http, first: false);
        var draft = await steps.DraftAsync(input, null, Ct);
        var review = await steps.ReviewAsync(input, draft, Ct);
        Assert.Empty(review.Problems);
        Assert.Equal(10, review.Score);
        Assert.All(http.Requests, request => Assert.Equal("deep-reply", request["model"]!.GetValue<string>()));
        Assert.DoesNotContain(input.Brief.Vacancy.Url, review.Text);
    }

    [Fact]
    public async Task Changed_context_recipe_is_rejected_before_paid_work()
    {
        using var http = new Http();
        var (steps, input, _) = await Setup(http);
        input.Brief.Situation = "Changed context";
        Assert.Throws<LetterPreparationSupersededException>(() => steps.EnsureCompatible(input));
        await Assert.ThrowsAsync<LetterPreparationSupersededException>(() => steps.DraftAsync(input, null, Ct));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Exact_review_preserves_composed_text_and_only_calls_the_maximum_effort_reviewer()
    {
        using var http = new Http();
        var (steps, input, _) = await Setup(http);
        var operation = new JobFinderLlm(new LlmClient(new OpenRouterTransport(http, Llm.LlmRuntimePolicyTests.Bootstrap()),
            new ModelHealth(TimeProvider.System), Llm.LlmRuntimePolicyTests.Bootstrap(), new Usage(), NullLogger<LlmClient>.Instance), new PromptCatalog(Fixture.PromptsDir))
            .RestoreOperation(input.Operation);
        input.Version = 2;
        input.ExactText = JobFinder.Core.Text.LetterLayout.Compose(http.Message.Greeting,
            JobFinder.Core.Text.LetterLayout.VacancyReference(input.Brief.ToBrief().Vacancy), http.Message.Paragraphs, http.Message.Closing);
        input.ReviewContextHash = input.Recipe.Hash;
        input.Recipe = LetterWriter.ReviewRecipe(operation, input.Profile, input.Facts, input.Brief.ToBrief(), input.ExactText);
        var result = await steps.ReviewAsync(input, new LetterCandidate { Text = input.ExactText, Written = input.ExactText }, Ct);
        Assert.Equal(input.ExactText, result.Text);
        Assert.True(LetterPreparationWorkflow.Ready(result));
        Assert.Equal(input.ReviewContextHash, result.Review!.ContextHash);
        var request = Assert.Single(http.Requests);
        Assert.Equal("recruiter_check", request["response_format"]!["json_schema"]!["name"]!.GetValue<string>());
        Assert.Equal("xhigh", request["reasoning"]!["effort"]!.GetValue<string>());
        await Assert.ThrowsAsync<UnsupportedLetterPreparationException>(() => steps.DraftAsync(input, null, Ct));
        Assert.Single(http.Requests);
    }

    [Fact]
    public void Old_pending_letter_checkpoint_and_missing_mutable_fields_remain_readable()
    {
        var old = JsonSerializer.Deserialize("{}", LetterPreparationJsonContext.Default.LetterPreparationInput)!;
        Assert.NotNull(old.Brief);
        Assert.NotNull(old.Facts.Facts);
        Assert.NotNull(old.Profile.Skills);
        var candidate = LetterPreparationRequests.ReadCandidate("{}");
        Assert.Empty(candidate.Evidence);
        Assert.Empty(candidate.Problems);
        Assert.False(LetterPreparationWorkflow.Ready(candidate));
    }

    private sealed class Usage : ILlmUsageSink
    {
        public Task RecordAsync(LlmAttempt attempt, CancellationToken ct) => Task.CompletedTask;
        public Task<decimal> SpentTodayUsdAsync(CancellationToken ct) => Task.FromResult(0m);
    }

    private sealed class Http : HttpMessageHandler, IHttpClientFactory
    {
        public List<JsonObject> Requests { get; } = [];
        public RecruiterMessage Message { get; } = new()
        {
            Hook = "Надёжность конкурентных платежей", Needs = ["Надёжность операций"],
            Facts = [new PickedFact { Fact = Quote, Need = "Надёжность операций", SourceQuote = Quote, WhyInteresting = "Близкий опыт" }],
            Greeting = "Здравствуйте!",
            Paragraphs = ["В игровом backend устранил риск двойных списаний и отрицательного баланса при конкурентных ставках. Для вашей платформы, где важна надёжность платёжных операций, это мой самый близкий опыт: отвечал за конкурентное изменение баланса и проверку операций."],
            Closing = "Подскажите, пожалуйста, какой следующий этап отбора?",
        };
        public MessageCheck Check { get; } = new()
        {
            Natural = true, HasReason = true, ProjectCases = 1,
            Claims = [new CheckedClaim { Claim = Quote, Quote = Quote, Supported = true, Kind = "experience" }],
            Verdict = new RecruiterVerdict { WouldReply = true, Checks = RecruiterVerdict.Criteria.Keys.Select(id => new VerdictCheck { Id = id, Passed = true }).ToList() },
        };
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            Requests.Add(body);
            var text = body["response_format"]!["json_schema"]!["name"]!.GetValue<string>() == "recruiter_message"
                ? JsonSerializer.Serialize(Message, CareerJsonContext.Default.RecruiterMessage)
                : JsonSerializer.Serialize(Check, CareerJsonContext.Default.MessageCheck);
            var response = new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = text }, ["finish_reason"] = "stop" }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 10 },
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString()) };
        }
    }
}
