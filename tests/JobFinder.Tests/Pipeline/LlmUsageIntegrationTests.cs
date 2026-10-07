using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using JobFinder.Core.Artifacts;
using JobFinder.Core.Domain;
using JobFinder.Core.Time;
using JobFinder.Llm;
using JobFinder.Llm.Client;
using JobFinder.Llm.Contracts;
using JobFinder.Llm.Prompts;
using JobFinder.Llm.Tasks;
using JobFinder.Llm.Tasks.Discovery;
using JobFinder.Pipeline.Llm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobFinder.Tests.Pipeline;

public sealed class LlmUsageIntegrationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private DbLlmUsageSink Sink => new(pg.Db, new LocalClock(TimeProvider.System, "Europe/Minsk"));

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.LlmCalls.ExecuteDeleteAsync(Ct);
        await context.GeneratedAnswers.ExecuteDeleteAsync(Ct);
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Actual_task_attempts_and_saved_artifact_are_linked_by_operation_in_the_migrated_database()
    {
        using var http = new Requests();
        var options = new LlmOptions { ApiKey = "synthetic-secret", Routes = new()
        {
            [LlmPurpose.Extract] = [new() { Name = "failed" }, new() { Name = "requested" }],
        } };
        var client = new LlmClient(new OpenRouterTransport(http, options), new ModelHealth(TimeProvider.System), options, Sink,
            NullLogger<LlmClient>.Instance);
        var operation = await new JobFinderLlm(client, new PromptCatalog(Fixture.PromptsDir)).ForOperationAsync(Ct);
        await operation.ExtractPostAsync("synthetic-private-source", "channel", Ct);
        var artifactRecipe = operation.Recipe([(ExtractPostTask.Definition, LlmPurpose.Extract)],
            new Dictionary<string, string?> { ["source"] = "synthetic-private-source" });
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.GeneratedAnswers.Add(new GeneratedAnswer { Key = "isolated-artifact", Value = "{}", Recipe = artifactRecipe });
        await context.SaveChangesAsync(Ct);
        var artifact = await context.GeneratedAnswers.AsNoTracking().SingleAsync(Ct);
        var calls = await context.LlmCalls.Where(call => call.OperationId == artifact.Recipe!.OperationId).OrderBy(call => call.AttemptNumber).ToListAsync(Ct);
        Assert.Equal(2, calls.Count);
        Assert.Equal([1, 2], calls.Select(call => call.AttemptNumber));
        Assert.Single(calls.Select(call => call.CallId).Distinct());
        Assert.False(calls[0].Ok);
        Assert.True(calls[1].Ok);
        Assert.Equal("requested", calls[1].RequestedModel);
        Assert.Equal("actual-provider-model", calls[1].Model);
        Assert.All(calls, call => Assert.Equal(0.0001m, call.CostUsd));
        Assert.Equal(0.0002m, await Sink.SpentTodayUsdAsync(Ct));
        Assert.All(calls, call => Assert.Equal(artifactRecipe.OperationId, call.Recipe!.OperationId));
        Assert.Equal(calls[0].Recipe!.Hash, calls[1].Recipe!.Hash);
        Assert.DoesNotContain("synthetic-private-source", JsonSerializer.Serialize(calls[1].Recipe, RecipeJsonContext.Default.ArtifactRecipe));
        var columnType = await context.Database.SqlQuery<string>(
            $"SELECT data_type AS \"Value\" FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'llm_calls' AND column_name = 'recipe'").SingleAsync(Ct);
        Assert.Equal("jsonb", columnType);
    }

    [Fact]
    public async Task Legacy_usage_rows_remain_readable_and_included_in_daily_spend()
    {
        await Sink.RecordAsync(new LlmAttempt(LlmPurpose.Score, "old-model", 10, 5, 0.012m, 50, true, null), Ct);
        await Sink.RecordAsync(new LlmAttempt(LlmPurpose.Score, "old-model", 20, 10, 0.003m, 60, false, "old error"), Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var rows = await context.LlmCalls.ToListAsync(Ct);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Null(row.OperationId));
        Assert.All(rows, row => Assert.Null(row.CallId));
        Assert.All(rows, row => Assert.Null(row.Recipe));
        Assert.Equal(0.015m, await Sink.SpentTodayUsdAsync(Ct));
    }

    [Fact]
    public async Task A_duplicate_attempt_cannot_double_count_the_paid_call_but_next_fallback_is_distinct()
    {
        var operationId = Guid.NewGuid();
        var callId = Guid.NewGuid();
        var attempt = new LlmAttempt(LlmPurpose.Score, "model", 10, 5, 0.001m, 50, false, "failed")
        {
            OperationId = operationId, CallId = callId, AttemptNumber = 1, RequestedModel = "model",
        };
        await Sink.RecordAsync(attempt, Ct);
        await Assert.ThrowsAsync<DbUpdateException>(() => Sink.RecordAsync(attempt, Ct));
        await Sink.RecordAsync(attempt with { AttemptNumber = 2, Model = "fallback", Ok = true, Error = null }, Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(2, await context.LlmCalls.CountAsync(Ct));
        Assert.Equal(0.002m, await Sink.SpentTodayUsdAsync(Ct));
    }

    private sealed class Requests : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var failed = body.RootElement.GetProperty("model").GetString() == "failed";
            const string extraction = """
                {"isVacancy":false,"isDotnet":false,"title":null,"company":null,"city":null,"country":null,"workFormats":[],
                 "salaryFrom":null,"salaryTo":null,"currency":null,"salaryGross":null,"englishLevel":"unknown","seniority":"unknown",
                 "skills":[],"contacts":[],"applyUrl":null}
                """;
            var completion = new JsonObject
            {
                ["model"] = "actual-provider-model",
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["message"] = new JsonObject { ["content"] = failed ? "{}" : extraction }, ["finish_reason"] = failed ? "length" : "stop",
                }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 100, ["completion_tokens"] = 50, ["cost"] = 0.0001m },
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(completion.ToJsonString()) };
        }
    }
}
