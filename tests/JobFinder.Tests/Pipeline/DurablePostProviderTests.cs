using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using JobFinder.Core.Domain;
using JobFinder.Llm;
using JobFinder.Llm.Configuration;
using JobFinder.Pipeline.Modules.Discovery;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurablePostProcessingTests
{
    [Fact]
    public async Task Actual_task_transport_preserves_frozen_effort_route_and_operation_usage_without_a_warm_paid_call()
    {
        var producer = await PostProcessingTestHost.BuildAsync(pg, false);
        _hosts.Add(producer);
        await producer.StartAsync(Ct);
        var post = await PostAsync();
        await using (var source = await pg.Db.CreateDbContextAsync(Ct))
            await source.RawPosts.Where(row => row.Id == post.Id).ExecuteUpdateAsync(update => update.SetProperty(row => row.Text,
                "Ищем Senior C#/.NET разработчика для backend-команды Acme. Удалённая работа, развитие сервисов и PostgreSQL, тесты и code review. Резюме jobs@acme.io."), Ct);
        var id = await RequestAsync(producer, post);
        var input = JsonNode.Parse((await RootAsync(id)).InputJson)!;
        var policies = producer.Services.GetRequiredService<LlmPolicyStore>();
        var before = await policies.ReadAsync(Ct);
        var changed = LlmRuntimePolicyCompiler.Clone(before.Value);
        foreach (var model in changed.Models.Values) model.Name = "synthetic/changed-after-capture";
        await policies.PublishAsync(changed, before.Version, "synthetic-route-change", Ct);
        await producer.StopAsync(Ct);
        var http = new ExtractionProvider();
        var consumer = await PostProcessingTestHost.BuildAsync(pg, true, configure: services =>
        {
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(http);
        });
        _hosts.Add(consumer);
        await consumer.StartAsync(Ct);
        await UntilAsync(async () => (await RootAsync(id)).Stage == PostProcessingStage.Completed);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var call = Assert.Single(await context.LlmCalls.Where(row => row.OperationId == id).ToListAsync(Ct));
        Assert.Equal(LlmPurpose.Extract, call.Purpose);
        Assert.Equal("synthetic/extract", call.RequestedModel);
        Assert.Equal(0.0001m, call.CostUsd);
        Assert.Equal("max", http.Body?["reasoning"]?["effort"]?.GetValue<string>());
        Assert.Equal("synthetic/extract", http.Body?["model"]?.GetValue<string>());
        Assert.NotNull(input["recipe"]);
        await consumer.Services.GetRequiredService<IMessageBus>().PublishAsync(new ProcessPost { RequestId = id, Stage = PostProcessingStage.Extracting });
        await UntilAsync(async () => await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_queues.wolverine_queue_discovery").SingleAsync(Ct) == 0);
        Assert.Equal(1, http.Calls);
        Assert.Single(await context.LlmCalls.Where(row => row.OperationId == id).ToListAsync(Ct));
    }

    private sealed class ExtractionProvider : HttpMessageHandler, IHttpClientFactory
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public JsonNode? Body { get; private set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host != "provider.invalid") throw new InvalidOperationException("post provider fixture refuses other hosts");
            Interlocked.Increment(ref _calls);
            Body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct));
            var extraction = System.Text.Json.JsonSerializer.Serialize(new
            {
                isVacancy = true, isDotnet = true, title = "Senior .NET Developer", company = "Acme", city = (string?)null, country = (string?)null,
                workFormats = Array.Empty<string>(), salaryFrom = (decimal?)null, salaryTo = (decimal?)null, currency = (string?)null, salaryGross = (bool?)null,
                englishLevel = "unknown", seniority = "senior", skills = new[] { "C#" }, contacts = new[] { "jobs@acme.io" }, applyUrl = (string?)null,
            });
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject
                {
                    ["model"] = "synthetic/extract", ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = extraction }, ["finish_reason"] = "stop" }),
                    ["usage"] = new JsonObject { ["prompt_tokens"] = 100, ["completion_tokens"] = 50, ["cost"] = 0.0001 },
                }.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        }
    }
}
