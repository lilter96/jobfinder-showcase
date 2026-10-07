using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using JobFinder.Core.Domain;
using JobFinder.Llm;
using JobFinder.Llm.Client;
using JobFinder.Pipeline.Modules.Engagement;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableCommentPreparationIntegrationTests
{
    [Fact]
    public async Task Actual_adapter_pins_both_paid_rounds_and_records_usage_without_a_warm_call()
    {
        using var http = new CommentProvider();
        var producer = await BuildAsync(false, http: http);
        _hosts.Add(producer);
        await producer.StartAsync(Ct);
        var id = await RequestAsync(producer);
        await producer.StopAsync(Ct);
        var policy = producer.Services.GetRequiredService<LlmPolicyStore>();
        var snapshot = await policy.ReadAsync(Ct);
        snapshot.Value.Models.Values.Single(model => model.Name == "synthetic/comment").Name = "synthetic/new-comment";
        await policy.PublishAsync(snapshot.Value, snapshot.Version, "concurrent-test-edit", Ct);
        var consumer = await BuildAsync(true, http: http);
        _hosts.Add(consumer);
        await consumer.StartAsync(Ct);
        var requests = consumer.Services.GetRequiredService<CommentPreparationRequests>();
        var result = await requests.WaitAsync(id, Ct);
        Assert.True(result.Plan.WorthCommenting);
        Assert.Equal(Good().Variants, result.Plan.Variants);
        Assert.NotEmpty(result.Remarks);
        Assert.Equal(2, http.Requests.Count);
        Assert.All(http.Requests, body =>
        {
            Assert.Equal("synthetic/comment", body["model"]!.GetValue<string>());
            Assert.Equal("max", body["reasoning"]!["effort"]!.GetValue<string>());
        });
        var first = JsonSerializer.Deserialize((await ReadAsync(id)).FirstJson!, CommentPreparationJsonContext.Default.CommentPreparationRound)!;
        Assert.Contains(string.Join("\n", first.Remarks), http.Requests.Last()["messages"]!.ToJsonString(new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }).Replace("\\n", "\n", StringComparison.Ordinal));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var calls = await context.LlmCalls.Where(call => call.OperationId == id).ToListAsync(Ct);
        Assert.Equal(2, calls.Count);
        Assert.Equal(2, calls.Select(call => call.CallId).Distinct().Count());
        Assert.All(calls, call =>
        {
            Assert.True(call.Ok);
            Assert.Equal(LlmPurpose.Comment, call.Purpose);
            Assert.Equal("synthetic/comment", call.RequestedModel);
            Assert.Equal("synthetic/provider-alias", call.Model);
            Assert.Equal(0.0001m, call.CostUsd);
            Assert.Equal(id, call.Recipe!.OperationId);
            Assert.Equal(snapshot.Version, call.Recipe.PolicyRevision);
        });
        var warm = await requests.GetAsync(id, Ct);
        Assert.Equal(result.Plan.Variants, warm!.Plan.Variants);
        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public async Task Corrupted_saved_inputs_are_quarantined_without_a_paid_draft()
    {
        var producer = await HostAsync(false);
        var id = await RequestAsync(producer);
        await producer.StopAsync(Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.CommentPreparationRequests.Where(row => row.Id == id).ExecuteUpdateAsync(update => update.SetProperty(row => row.InputJson, "{}"), Ct);
        await HostAsync(true);
        await UntilAsync(async () => (await ReadAsync(id)).Stage == CommentPreparationStage.Failed);
        Assert.Empty(_steps.Drafts);
        Assert.Single(await context.Notifications.ToListAsync(Ct));
        await UntilAsync(async () => await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_messages.wolverine_dead_letters").SingleAsync(Ct) == 1);
    }

    [Fact]
    public async Task A_tampered_completed_result_cannot_bypass_the_surviving_set_check()
    {
        var host = await HostAsync(true);
        var id = await RequestAsync(host);
        var requests = host.Services.GetRequiredService<CommentPreparationRequests>();
        await requests.WaitAsync(id, Ct);
        var tampered = JsonSerializer.Serialize(new CommentPreparationResult
        {
            Plan = Good() with { Variants = [Good().Variants[1]] },
        }, CommentPreparationJsonContext.Default.CommentPreparationResult);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.CommentPreparationRequests.Where(row => row.Id == id).ExecuteUpdateAsync(update => update.SetProperty(row => row.ResultJson, tampered), Ct);
        await Assert.ThrowsAsync<JsonException>(() => requests.GetAsync(id, Ct));
        Assert.Equal(1, _steps.Drafts[id]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Guarded_rollback_refuses_requests_and_orphaned_native_envelopes(bool orphaned)
    {
        var producer = await HostAsync(false);
        var id = await RequestAsync(producer);
        await producer.StopAsync(Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (orphaned) await context.CommentPreparationRequests.ExecuteDeleteAsync(Ct);
        try
        {
            var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20261001013146_LetterEditRevision", Ct));
            Assert.Contains("Cannot roll back durable comments", error.MessageText);
            Assert.Contains("20261001064525_DurableCommentPreparation", await context.Database.GetAppliedMigrationsAsync(Ct));
            Assert.Equal(orphaned ? 0 : 1, await context.CommentPreparationRequests.CountAsync(Ct));
            Assert.Equal(1, await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_queues.wolverine_queue_engagement").SingleAsync(Ct));
            if (!orphaned) Assert.Equal(id, (await context.CommentPreparationRequests.SingleAsync(Ct)).Id);
        }
        finally { await context.Database.MigrateAsync(Ct); }
    }

    [Fact]
    public async Task Empty_guarded_rollback_and_reapply_preserve_the_current_model()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        try
        {
            await context.GetService<IMigrator>().MigrateAsync("20261001013146_LetterEditRevision", Ct);
            Assert.DoesNotContain("20261001064525_DurableCommentPreparation", await context.Database.GetAppliedMigrationsAsync(Ct));
        }
        finally { await context.Database.MigrateAsync(Ct); }
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Empty(await context.CommentPreparationRequests.ToListAsync(Ct));
    }

    private sealed class CommentProvider : HttpMessageHandler, IHttpClientFactory
    {
        public ConcurrentQueue<JsonObject> Requests { get; } = new();
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("provider.invalid", request.RequestUri!.Host);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            Requests.Enqueue(body);
            var plan = Requests.Count == 1 ? Bad() : Good();
            var content = new JsonObject
            {
                ["audience"] = plan.Audience, ["worthCommenting"] = plan.WorthCommenting, ["reason"] = plan.Reason,
                ["variants"] = new JsonArray(plan.Variants.Select(variant => (JsonNode?)JsonValue.Create(variant)).ToArray()),
            }.ToJsonString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject
                {
                    ["model"] = "synthetic/provider-alias",
                    ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = content }, ["finish_reason"] = "stop" }),
                    ["usage"] = new JsonObject { ["prompt_tokens"] = 100, ["completion_tokens"] = 60, ["cost"] = 0.0001m },
                }.ToJsonString()),
            };
        }
    }
}
