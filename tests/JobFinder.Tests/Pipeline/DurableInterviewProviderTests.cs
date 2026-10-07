using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JobFinder.Core.Domain;
using JobFinder.Core.Options;
using JobFinder.Core.Profile;
using JobFinder.Data;
using JobFinder.Llm;
using JobFinder.Llm.Client;
using JobFinder.Llm.Configuration;
using JobFinder.Llm.Contracts.Wire;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Documents;
using JobFinder.Pipeline.Modules.Candidate;
using JobFinder.Pipeline.Modules.Preparation;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableInterviewPreparationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_interview_tasks_usage_and_Chromium_pdf_preserve_frozen_routes_and_reuse_successful_answers(bool outage)
    {
        var provider = new InterviewProvider { SalaryUnavailable = outage };
        var producer = await RealInterviewHostAsync(false, provider);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var profileVersion = await context.Documents.Where(row => row.Key == ProfileProvider.Key).Select(row => row.Version).SingleAsync(Ct);
        await (await pg.CandidateAsync()).PublishProfileAsync(new CandidateProfile
        {
            Name = "Synthetic Candidate", Title = "Backend Developer", Reviewed = true, Location = "Минск",
            Summary = InterviewProvider.Start, Achievements = [InterviewProvider.Core],
            Skills = [new() { Name = "C#", Level = "good" }, new() { Name = ".NET", Level = "good" }],
            Experience = [new() { Company = "Synthetic Platform", Role = "Backend Developer", Period = "2023–2026", Highlights = InterviewProvider.Core }],
        }, profileVersion, "synthetic-interview-provider", Ct);
        var policies = producer.Services.GetRequiredService<LlmPolicyStore>();
        var original = await policies.ReadAsync(Ct);
        var policy = LlmRuntimePolicyCompiler.Import(producer.Services.GetRequiredService<LlmOptions>());
        policy.MaxAttempts = 1;
        original = await policies.PublishAsync(policy, original.Version, "synthetic-interview-routes", Ct);
        var interview = await InterviewAsync();
        var id = await RequestAsync(producer, interview.Id);
        var changed = LlmRuntimePolicyCompiler.Clone(original.Value);
        foreach (var model in changed.Models.Values) model.Name = "synthetic/changed-after-capture";
        await policies.PublishAsync(changed, original.Version, "synthetic-interview-route-change", Ct);
        var paths = producer.Services.GetRequiredService<PathsOptions>();
        await producer.StopAsync(Ct);
        var consumer = await RealInterviewHostAsync(true, provider, paths);
        InterviewPreparationRequest? paused = null;
        if (outage)
        {
            await UntilAsync(async () => (await RootAsync(id)).RetryAt != null);
            paused = await RootAsync(id);
            Assert.Equal(InterviewPreparationStage.Answering, paused.Stage);
            Assert.Equal(0, paused.Failures);
            Assert.NotNull(paused.PlanJson);
            Assert.Null(paused.CopilotJson);
            Assert.Equal(8, await context.GeneratedAnswers.CountAsync(row => row.Scope == "interview", Ct));
            Assert.Equal(10, provider.Calls.Count);
            provider.SalaryUnavailable = false;
            consumer.Services.GetRequiredService<ModelHealth>().ReportSuccess("synthetic/interviewmedium");
            await WakeAsync(consumer, id);
        }
        await UntilAsync(async () => (await RootAsync(id)).Stage == InterviewPreparationStage.Completed);
        var root = await RootAsync(id);
        await consumer.Services.GetRequiredService<InterviewPreparationRequests>().WaitAsync(id, Ct);
        var source = await context.Interviews.AsNoTracking().SingleAsync(row => row.Id == interview.Id, Ct);
        var pack = JsonSerializer.Deserialize(source.PackJson!, InterviewJsonContext.Default.InterviewCopilot)!;
        Assert.Equal(8, pack.Preparation.Count);
        Assert.NotNull(pack.Salary);
        Assert.Equal("personal_unknown", pack.Salary.A!.EvidenceLevel);
        Assert.All(pack.Preparation, item =>
        {
            Assert.Equal("verified", item.A!.EvidenceLevel);
            Assert.Empty(item.StyleProblems);
            Assert.Empty(CopilotRules.SpeechProblems(item.A, item.Q));
            Assert.Equal(outage, item.Reused);
        });
        if (paused is not null)
        {
            Assert.Equal(paused.InputHash, root.InputHash);
            Assert.Equal(paused.PlanHash, root.PlanHash);
        }
        var calls = await context.LlmCalls.Where(row => row.OperationId == id).ToListAsync(Ct);
        Assert.Equal(outage ? 11 : 10, calls.Count);
        Assert.Equal(calls.Count, provider.Calls.Count);
        Assert.Equal(outage ? 1 : 0, calls.Count(call => !call.Ok));
        Assert.Equal(calls.Sum(call => call.CostUsd), pack.CostUsd);
        Assert.Equal(0.0001m * calls.Count, pack.CostUsd);
        Assert.Equal(calls.Count, calls.Select(call => call.CallId).Distinct().Count());
        Assert.All(calls, call =>
        {
            Assert.Equal("synthetic/" + call.Purpose.ToLowerInvariant(), call.RequestedModel);
            Assert.NotNull(call.Recipe);
            Assert.Equal(id, call.Recipe.OperationId);
            Assert.Equal(100, call.PromptTokens);
            Assert.Equal(50, call.CompletionTokens);
        });
        Assert.Equal(new[] { LlmPurpose.Interview, LlmPurpose.InterviewHigh, LlmPurpose.InterviewLow, LlmPurpose.InterviewMedium }.Order(),
            calls.Select(call => call.Purpose).Distinct().Order());
        Assert.All(provider.Calls, call => Assert.Equal("max", call.Body["reasoning"]!["effort"]!.GetValue<string>()));
        Assert.Single(provider.Calls, call => call.QuestionId == "plan");
        Assert.All(provider.Calls.Where(call => call.QuestionId != CopilotRules.SalaryId).GroupBy(call => call.QuestionId), group => Assert.Single(group));
        Assert.Equal(outage ? 2 : 1, provider.Calls.Count(call => call.QuestionId == CopilotRules.SalaryId));
        Assert.Equal(10, await context.GeneratedAnswers.CountAsync(Ct));
        Assert.Equal(1, await context.Notifications.CountAsync(row => row.AttachmentPath != null, Ct));
        var path = StoredPath.Resolve(root.PdfPath!);
        using (var pdf = PdfDocument.Open(path))
        {
            Assert.InRange(pdf.NumberOfPages, 2, 12);
            var text = System.Text.RegularExpressions.Regex.Replace(
                string.Join("\n", pdf.GetPages().Select(page => ContentOrderTextExtractor.GetText(page))), @"\s+", " ");
            Assert.Contains("Synthetic company", text);
            Assert.Contains("За 10 минут до интервью", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Вопросы и ответы", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("атомарную операцию", text);
            Assert.Contains("Какие у вас зарплатные ожидания?", text);
            Assert.Contains("Нужна ваша позиция (дашборд)", text);
            Assert.Contains("Сначала уточнить вилку и формат работы", text);
            Assert.Contains("Обсудить ожидания после знакомства с задачами", text);
            Assert.DoesNotContain("synthetic/changed-after-capture", text);
            foreach (var page in pdf.GetPages())
            {
                Assert.NotEmpty(page.Letters);
                Assert.All(page.Letters, letter =>
                {
                    Assert.InRange(letter.BoundingBox.Left, -1, page.Width + 1);
                    Assert.InRange(letter.BoundingBox.Right, -1, page.Width + 1);
                    Assert.InRange(letter.BoundingBox.Bottom, -1, page.Height + 1);
                    Assert.InRange(letter.BoundingBox.Top, -1, page.Height + 1);
                });
            }
        }
        var beforeWarm = provider.Calls.Count;
        Assert.Equal(id, await RequestAsync(consumer, interview.Id));
        await consumer.Services.GetRequiredService<InterviewPreparationRequests>().WaitAsync(id, Ct);
        Assert.Equal(beforeWarm, provider.Calls.Count);
        TestContext.Current.TestOutputHelper!.WriteLine("Real Chromium interview PDF: " + path);
    }

    private async Task<IHost> RealInterviewHostAsync(bool consumer, InterviewProvider provider, PathsOptions? paths = null)
    {
        var host = await PostProcessingTestHost.BuildAsync(pg, consumer, configure: services =>
        {
            if (paths is not null) services.AddSingleton(paths);
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(provider);
        });
        _hosts.Add(host);
        var configured = host.Services.GetRequiredService<PathsOptions>();
        configured.Assets = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../assets"));
        Assert.NotEmpty(host.Services.GetRequiredService<DocumentAssets>().FontCss);
        Assert.IsType<InterviewPreparationSteps>(host.Services.GetRequiredService<IInterviewPreparationSteps>());
        await host.StartAsync(Ct);
        return host;
    }

    private sealed class InterviewProvider : HttpMessageHandler, IHttpClientFactory
    {
        public const string Start = "Я занимаюсь backend-разработкой на C# и .NET.";
        public const string Core = "В последнем сервисе я работал с конкурентными запросами к балансу. Я объединил проверку и списание в одну атомарную операцию. Затем добавил тесты на повторные запросы, чтобы изменение баланса нельзя было выполнить дважды.";
        public ConcurrentQueue<(string QuestionId, JsonNode Body)> Calls { get; } = new();
        public bool SalaryUnavailable { get; set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        private static InterviewPlanWire Plan() => new()
        {
            Role = "Backend Developer", OverallRisk = "low",
            Priorities = [new() { Topic = "Конкурентные запросы", Priority = "high", Reason = "Сервис меняет общий баланс", Evidence = "A1", CandidateLevel = "strong", Basis = "confirmed" }],
            Questions = new[]
            {
                (CopilotRules.About, "Расскажите о себе", "hr"),
                (CopilotRules.WhyCompany, "Почему вы откликнулись именно к нам?", "motivation"),
                (CopilotRules.WhyRole, "Что вы ищете в новой работе и почему эта роль?", "motivation"),
                (CopilotRules.WhyHire, "Почему нам стоит нанять именно вас?", "motivation"),
                (CopilotRules.FirstSteps, "Какими будут ваши первые шаги на новом месте?", "motivation"),
                (CopilotRules.SalaryId, "Какие у вас зарплатные ожидания?", "negotiation"),
                ("concurrency", "Как вы устраняли повторные списания?", "technical"),
                ("atomicity", "Как вы проверяли атомарность операции?", "technical_depth"),
                ("location", "Где вы сейчас живёте?", "hr"),
            }.Select(item => new InterviewPlanWireQuestionsItem
            {
                Id = item.Item1, Question = item.Item2, Category = item.Item3, Priority = "medium", Likelihood = "high", Basis = "confirmed",
                Source = "Синтетическая вакансия и подтверждённый профиль", Tests = "Конкретный опыт", Risk = "low", CandidateLevel = "strong", Evidence = ["A1"],
            }).ToList(),
            EmployerQuestions = new() { MustAsk = [new() { Question = "Как проверяете изменения баланса?", Why = "Понять текущий процесс проверки" }] },
            Company = new() { Brief = "Синтетическая компания для проверки конвейера, не реальный работодатель." },
        };

        private static SpokenAnswerWire Answer(PlannedQuestion question) => question.Id == CopilotRules.SalaryId ? new()
        {
            EvidenceLevel = "personal_unknown", PositionOptions = ["Сначала уточнить вилку и формат работы", "Обсудить ожидания после знакомства с задачами"],
        } : new()
        {
            Start = Start, Core = Core, EvidenceLevel = "verified", UsedFacts = ["location"],
            FollowUps = question.Category is "technical" or "technical_depth" ? new[]
            {
                "Как предотвращали повторный запрос?", "Как проверяли конкурентные запросы?", "Что изменилось после исправления?",
            }.Select(value => new SpokenAnswerWireFollowUpsItem { Question = value, Start = Start, Core = Core }).ToList() : [],
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host != "provider.invalid") throw new InvalidOperationException("interview provider fixture refuses external hosts");
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var isPlan = body["response_format"]!["json_schema"]!["name"]!.GetValue<string>() == "interview_plan";
            PlannedQuestion? question = null;
            if (!isPlan)
            {
                var user = body["messages"]!.AsArray().Single(message => message!["role"]!.GetValue<string>() == "user")!["content"]!.GetValue<string>();
                var start = user.IndexOf("<question>\n", StringComparison.Ordinal) + "<question>\n".Length;
                var end = user.IndexOf("\n</question>", start, StringComparison.Ordinal);
                question = JsonSerializer.Deserialize(user[start..end], InterviewJsonContext.Default.PlannedQuestion)!;
                Assert.Contains("Минск", user.Replace("Minsk", "Минск", StringComparison.Ordinal));
                Assert.Contains("атомарную операцию", user);
            }
            Calls.Enqueue((question?.Id ?? "plan", body));
            var content = isPlan ? JsonSerializer.Serialize(Plan(), GeneratedWireJsonContext.Default.InterviewPlanWire)
                : JsonSerializer.Serialize(Answer(question!), GeneratedWireJsonContext.Default.SpokenAnswerWire);
            if (SalaryUnavailable && question?.Id == CopilotRules.SalaryId) content = "{\"start\":\"paid but incomplete\"}";
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject
                {
                    ["model"] = body["model"]!.GetValue<string>(),
                    ["choices"] = new JsonArray(new JsonObject { ["finish_reason"] = "stop", ["message"] = new JsonObject { ["content"] = content } }),
                    ["usage"] = new JsonObject { ["prompt_tokens"] = 100, ["completion_tokens"] = 50, ["cost"] = 0.0001 },
                }.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        }
    }
}
