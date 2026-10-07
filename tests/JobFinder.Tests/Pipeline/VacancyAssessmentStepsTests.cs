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
using JobFinder.Pipeline.Modules.Discovery;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobFinder.Tests.Pipeline;

public sealed class VacancyAssessmentStepsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(VacancyAssessmentSteps Steps, VacancyAssessmentInput Input, LlmOptions Options)> Setup(Http http)
    {
        var options = Llm.LlmRuntimePolicyTests.Bootstrap();
        var client = new LlmClient(new OpenRouterTransport(http, options), new ModelHealth(TimeProvider.System), options, new Usage(), NullLogger<LlmClient>.Instance);
        var llm = new JobFinderLlm(client, new PromptCatalog(Fixture.PromptsDir));
        var operation = await llm.ForOperationAsync(Ct);
        var input = new VacancyAssessmentInput
        {
            Profile = new CandidateProfile { Name = "Captured Candidate", Reviewed = true, Summary = "Built .NET backend services." },
            ProfileVersion = 7, FactsVersion = 4, SearchVersion = 2, AutomationVersion = 3,
            Vacancy = AssessmentVacancy.From(new Vacancy
            {
                Id = 123, Title = "Senior .NET Developer", CompanyName = "Synthetic Company", Description = "Develop backend services.",
                Fingerprint = "synthetic", Url = "https://example.invalid/123", Status = VacancyStatus.New,
                Sources = [new VacancySource { Source = SourceKind.HeadHunter, ExternalId = "123", Url = "https://example.invalid/123" }],
            }),
            Operation = operation.CaptureOperation(), SalaryAdjustment = -5, SalaryNote = "captured salary adjustment", CanAutoApply = true,
            CapturedAt = new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero), MarketNote = "captured market evidence",
        };
        input.Recipe = VacancyAssessmentSteps.Recipe(operation, input);
        var writer = new LetterWriter(llm, null!, null!, TimeProvider.System, NullLogger<LetterWriter>.Instance);
        return (new VacancyAssessmentSteps(llm, writer), input, options);
    }

    [Theory]
    [InlineData("blacklisted")]
    [InlineData("title")]
    [InlineData("english")]
    public async Task Deterministic_rejections_do_not_call_a_model(string reason)
    {
        using var http = new Http();
        var (steps, input, _) = await Setup(http);
        if (reason == "blacklisted") input.Vacancy.Blacklisted = true;
        else if (reason == "title") input.Search.Filter.TitleStopWords = ["Senior"];
        else input.Vacancy.EnglishRequired = EnglishLevel.B2;
        RefreshRecipe(input);
        var score = await steps.ScoreAsync(input, Ct);
        Assert.Equal(VacancyStatus.Rejected, score.Status);
        Assert.NotNull(score.RejectReason);
        Assert.Null(score.Evaluation);
        Assert.Empty(http.Requests);
    }

    [Theory]
    [InlineData("normal", VacancyStatus.Scored, Decision.AutoApply)]
    [InlineData("english", VacancyStatus.Rejected, Decision.None)]
    [InlineData("scam", VacancyStatus.Rejected, Decision.None)]
    [InlineData("suspicious", VacancyStatus.Scored, Decision.Approval)]
    [InlineData("quota", VacancyStatus.Scored, Decision.Approval)]
    [InlineData("paused", VacancyStatus.Scored, Decision.Approval)]
    [InlineData("responded", VacancyStatus.Applied, Decision.None)]
    public async Task Frozen_scoring_preserves_salary_english_legitimacy_history_and_decision_rules(string scenario, VacancyStatus status, Decision decision)
    {
        using var http = new Http();
        var (steps, input, options) = await Setup(http);
        if (scenario == "english") http.Score.EnglishRequired = "b2";
        if (scenario is "scam" or "suspicious") http.Score.Legitimacy = scenario;
        if (scenario == "quota") input.AutoToday = input.Automation.DailyAutoApplyLimit;
        if (scenario == "paused") input.Automation.AutoApplyPaused = true;
        if (scenario == "responded")
        {
            input.Responded = true;
            input.Search.Filter.TitleStopWords = ["Senior"];
        }
        options.Routes[LlmPurpose.Score][0].Name = "mutated-bootstrap-model";
        var score = await steps.ScoreAsync(input, Ct);
        Assert.Equal(status, score.Status);
        Assert.Equal(decision, score.Decision);
        Assert.Equal(85, score.Score);
        Assert.Equal("shared-model", score.Evaluation!.Model);
        Assert.Equal("captured salary adjustment", score.Evaluation.SalaryNote);
        Assert.Equal("captured market evidence", score.Evaluation.MarketNote);
        if (scenario == "responded") Assert.Contains(score.Evaluation.RedFlags, flag => flag.Contains("фильтр системы отсеял бы", StringComparison.Ordinal));
        var request = Assert.Single(http.Requests);
        Assert.Equal("shared-model", request["model"]!.ToString());
        Assert.Contains("Captured Candidate", request["messages"]!.ToJsonString());
    }

    [Fact]
    public async Task An_input_changed_without_rebuilding_its_recipe_is_rejected_before_paid_work()
    {
        using var http = new Http();
        var (steps, input, _) = await Setup(http);
        input.Profile.Summary = "changed candidate proof";
        await Assert.ThrowsAsync<VacancyAssessmentSupersededException>(() => steps.ScoreAsync(input, Ct));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Persisted_inputs_are_detached_round_trip_with_versions_and_exclude_deployment_secrets()
    {
        using var http = new Http();
        var (steps, input, options) = await Setup(http);
        var json = JsonSerializer.Serialize(input, VacancyAssessmentJsonContext.Default.VacancyAssessmentInput);
        Assert.DoesNotContain(options.ApiKey, json);
        Assert.DoesNotContain(options.Endpoint, json);
        Assert.DoesNotContain(options.Proxy!, json);
        var restored = JsonSerializer.Deserialize(json, VacancyAssessmentJsonContext.Default.VacancyAssessmentInput)!;
        Assert.Equal(input.Recipe.Hash, restored.Recipe.Hash);
        Assert.Equal(input.Operation.OperationId, restored.Operation.OperationId);
        Assert.Equal(7, restored.ProfileVersion);
        Assert.Equal(4, restored.FactsVersion);
        Assert.Equal("xhigh", restored.Operation.Policy.Routes[LlmPurpose.Application].Steps[0].Reasoning!.Effort);
        Assert.Equal(LetterWriter.QualityVersion, restored.Recipe.Rules["letter"]);
        await steps.ScoreAsync(restored, Ct);
        Assert.Single(http.Requests);
    }

    [Fact]
    public void Source_flags_and_manual_rescore_generation_change_the_witness_but_transport_fetch_times_do_not()
    {
        var vacancy = new Vacancy
        {
            Id = 123, Title = "Senior .NET", Description = "abcd", Url = "https://example.invalid/123", Fingerprint = "synthetic",
            Sources = [new VacancySource { Id = 7, Source = SourceKind.HeadHunter, ExternalId = "123", Url = "https://example.invalid/123" }],
        };
        var original = VacancyAssessments.Witness(AssessmentVacancy.From(vacancy));
        vacancy.Sources[0].FetchedAt = DateTimeOffset.UtcNow;
        Assert.Equal(original, VacancyAssessments.Witness(AssessmentVacancy.From(vacancy)));
        vacancy.Description = "wxyz";
        Assert.NotEqual(original, VacancyAssessments.Witness(AssessmentVacancy.From(vacancy)));
        vacancy.Description = "abcd";
        vacancy.Sources[0].HasEmployerTest = true;
        Assert.NotEqual(original, VacancyAssessments.Witness(AssessmentVacancy.From(vacancy)));
        vacancy.Sources[0].HasEmployerTest = false;
        vacancy.AssessmentRevision++;
        Assert.NotEqual(original, VacancyAssessments.Witness(AssessmentVacancy.From(vacancy)));
    }

    private static void RefreshRecipe(VacancyAssessmentInput input)
    {
        var options = Llm.LlmRuntimePolicyTests.Bootstrap();
        using var http = new Http();
        var client = new LlmClient(new OpenRouterTransport(http, options), new ModelHealth(TimeProvider.System), options, new Usage(), NullLogger<LlmClient>.Instance);
        var operation = new JobFinderLlm(client, new PromptCatalog(Fixture.PromptsDir)).RestoreOperation(input.Operation);
        input.Recipe = VacancyAssessmentSteps.Recipe(operation, input);
    }

    [Theory]
    [InlineData(VacancyStatus.Scored)]
    [InlineData(VacancyStatus.Applied)]
    public async Task Annotation_scoring_preserves_the_old_assessment_even_when_filters_or_scam_would_reject_it(VacancyStatus status)
    {
        using var http = new Http();
        var (steps, input, _) = await Setup(http);
        input.Version = 2;
        input.Vacancy.Status = status;
        input.Vacancy.Blacklisted = true;
        input.Search.Filter.TitleStopWords = ["Senior"];
        input.Annotation = new AssessmentAnnotationSnapshot
        {
            Evaluation = new VacancyEvaluation { Score = 91, Model = "historical-model", Summary = "Historical evaluation", Strengths = ["Original fact"], SalaryNote = "historical salary" },
            Score = 87, Decision = Decision.AutoApply, RejectReason = "historical note",
        };
        http.Score.Score = 3;
        http.Score.Legitimacy = "scam";
        http.Score.LegitimacyReasons = ["Synthetic scam indicator"];
        http.Score.EnglishRequired = "c1";
        http.Score.Keywords = ["C#", "SQL"];
        http.Score.Language = "ru";
        RefreshRecipe(input);
        var result = await steps.ScoreAsync(input, Ct);
        Assert.Equal(status, result.Status);
        Assert.Equal(87, result.Score);
        Assert.Equal(Decision.AutoApply, result.Decision);
        Assert.Equal(input.Vacancy.EnglishRequired, result.EnglishRequired);
        Assert.Equal(Legitimacy.Scam, result.Legitimacy);
        Assert.Equal("historical note", result.RejectReason);
        Assert.Equal(91, result.Evaluation!.Score);
        Assert.Equal("historical-model", result.Evaluation.Model);
        Assert.Equal("Historical evaluation", result.Evaluation.Summary);
        Assert.Equal("historical salary", result.Evaluation.SalaryNote);
        Assert.Equal(["C#", "SQL"], result.Evaluation.Keywords);
        Assert.Equal("ru", result.Evaluation.Language);
        Assert.Equal(input.MarketNote, result.Evaluation.MarketNote);
        Assert.Single(http.Requests);
        await Assert.ThrowsAsync<UnsupportedVacancyAssessmentException>(() => steps.LetterAsync(input, result, Ct));
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Annotation_protocol_is_explicit_and_does_not_change_v1_json_or_recipe_shape()
    {
        using var http = new Http();
        var (steps, input, _) = await Setup(http);
        var oldJson = JsonSerializer.Serialize(input, VacancyAssessmentJsonContext.Default.VacancyAssessmentInput);
        Assert.DoesNotContain("annotation", oldJson, StringComparison.OrdinalIgnoreCase);
        var restored = JsonSerializer.Deserialize(oldJson, VacancyAssessmentJsonContext.Default.VacancyAssessmentInput)!;
        Assert.Null(restored.Annotation);
        steps.EnsureCompatible(restored);
        input.Version = 2;
        await Assert.ThrowsAsync<UnsupportedVacancyAssessmentException>(() => steps.ScoreAsync(input, Ct));
        input.Version = 1;
        input.Annotation = new AssessmentAnnotationSnapshot();
        await Assert.ThrowsAsync<UnsupportedVacancyAssessmentException>(() => steps.ScoreAsync(input, Ct));
        Assert.Empty(http.Requests);
    }

    [Theory]
    [InlineData("title")]
    [InlineData("english")]
    [InlineData("unreviewed")]
    public async Task Read_only_preview_still_scores_deterministic_filter_failures_and_unreviewed_profiles_without_deciding_or_lettering(string scenario)
    {
        using var http = new Http();
        var (steps, input, _) = await Setup(http);
        input.Version = 3;
        input.Preview = new() { Key = "preview" };
        input.Vacancy = input.Vacancy with { Id = 0, CompanyId = null, Sources = [] };
        input.CanAutoApply = false;
        if (scenario == "title") input.Search.Filter.TitleStopWords = ["Senior"];
        if (scenario == "english") input.Vacancy.EnglishRequired = EnglishLevel.B2;
        if (scenario == "unreviewed") input.Profile.Reviewed = false;
        RefreshRecipe(input);
        var score = await steps.ScoreAsync(input, Ct);
        Assert.Equal(85, score.Score);
        Assert.Equal(90, score.Evaluation!.Score);
        Assert.Equal(VacancyStatus.Scored, score.Status);
        Assert.Equal(Decision.None, score.Decision);
        Assert.NotNull(score.CostUsd);
        Assert.Single(http.Requests);
        Assert.Equal(LlmTaskCatalog.Score.Id, Assert.Single(input.Recipe.Tasks).TaskId);
        await Assert.ThrowsAsync<UnsupportedVacancyAssessmentException>(() => steps.LetterAsync(input, score, Ct));
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Preview_protocol_is_explicit_and_legacy_json_hash_and_normal_rules_remain_unchanged()
    {
        using var http = new Http();
        var (steps, input, _) = await Setup(http);
        var json = JsonSerializer.Serialize(input, VacancyAssessmentJsonContext.Default.VacancyAssessmentInput);
        Assert.DoesNotContain("preview", json, StringComparison.OrdinalIgnoreCase);
        input.Preview = new() { Key = "wrong protocol" };
        await Assert.ThrowsAsync<UnsupportedVacancyAssessmentException>(() => steps.ScoreAsync(input, Ct));
        input.Version = 3;
        await Assert.ThrowsAsync<UnsupportedVacancyAssessmentException>(() => steps.ScoreAsync(input, Ct));
        input.Preview = null;
        await Assert.ThrowsAsync<UnsupportedVacancyAssessmentException>(() => steps.ScoreAsync(input, Ct));
        Assert.Empty(http.Requests);
        Assert.Equal("", JsonSerializer.Deserialize("{}", VacancyAssessmentJsonContext.Default.AssessmentPreviewSnapshot)!.Key);
    }

    private sealed class Usage : ILlmUsageSink
    {
        public Task RecordAsync(LlmAttempt attempt, CancellationToken ct) => Task.CompletedTask;
        public Task<decimal> SpentTodayUsdAsync(CancellationToken ct) => Task.FromResult(0m);
    }

    private sealed class Http : HttpMessageHandler, IHttpClientFactory
    {
        public VacancyScoring Score { get; } = new() { Score = 90, StackMatch = 90, SeniorityMatch = 90, DomainMatch = 80, FormatMatch = 90, Summary = "Relevant backend experience." };
        public List<JsonObject> Requests { get; } = [];
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject());
            var response = new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["message"] = new JsonObject { ["content"] = JsonSerializer.Serialize(Score, LlmJsonContext.Default.VacancyScoring) }, ["finish_reason"] = "stop",
                }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 10 },
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString()) };
        }
    }
}
