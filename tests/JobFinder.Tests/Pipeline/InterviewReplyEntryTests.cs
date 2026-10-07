using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JobFinder.Core.Domain;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Modules.Preparation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JobFinder.Tests.Pipeline;

public sealed partial class InterviewSourceIntegrationTests
{
    [Fact]
    public async Task Actual_reply_analysis_hands_off_invitation_and_event_atomically_and_deduplicates_the_message()
    {
        var source = await InterviewAsync();
        await using (var context = await pg.Db.CreateDbContextAsync(Ct)) await context.Interviews.ExecuteDeleteAsync(Ct);
        using var provider = new InvitationProvider();
        await SourcesAsync(consumer: true, configure: services =>
        {
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(provider);
        });
        var replies = _host!.Services.GetRequiredService<ReplyService>();
        await replies.HandleInboundAsync(source.ApplicationId, EventChannel.Telegram, "@synthetic", "Interview invitation", "synthetic:1", DateTimeOffset.UtcNow, Ct);
        await replies.HandleInboundAsync(source.ApplicationId, EventChannel.Telegram, "@synthetic", "Duplicate", "synthetic:1", DateTimeOffset.UtcNow, Ct);
        await WaitForReplyAsync(ReplyProcessingStage.Completed);
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        var interview = await final.Interviews.SingleAsync(Ct);
        Assert.Equal(InterviewKind.SystemDesign, interview.Kind);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero), interview.ScheduledAt);
        Assert.Equal(ApplicationStage.TechInterview, (await final.Applications.SingleAsync(Ct)).Stage);
        Assert.Single(await final.ApplicationEvents.Where(row => row.Kind == EventKind.MessageIn).ToListAsync(Ct));
        Assert.Single(await final.ApplicationEvents.Where(row => row.Kind == EventKind.StageChanged).ToListAsync(Ct));
        Assert.Single(await final.InterviewPreparationRequests.ToListAsync(Ct));
        Assert.Equal(1, provider.Calls);
        Assert.Equal("max", provider.Body?["reasoning"]?["effort"]?.GetValue<string>());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Actual_paid_invitation_rejects_changed_source_or_vacancy_without_overwriting(bool vacancyChanged, bool locationChanged)
    {
        var source = await InterviewAsync(pack: true);
        using var provider = new InvitationProvider();
        var commands = await SourcesAsync(consumer: true, configure: services =>
        {
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(provider);
        });
        provider.BeforeReply = async () =>
        {
            if (vacancyChanged)
            {
                await using var context = await pg.Db.CreateDbContextAsync(Ct);
                if (locationChanged) await context.Vacancies.ExecuteUpdateAsync(update => update.SetProperty(row => row.City, "Changed city"), Ct);
                else await context.Vacancies.ExecuteUpdateAsync(update => update.SetProperty(row => row.Description, "Changed vacancy"), Ct);
            }
            else await commands.SetTimeAsync(InterviewEditSnapshot.Capture(source), source.ScheduledAt!.Value.AddDays(1), Ct);
        };
        await _host!.Services.GetRequiredService<ReplyService>()
            .HandleInboundAsync(source.ApplicationId, EventChannel.Telegram, "@synthetic", "Invitation", "synthetic:race", DateTimeOffset.UtcNow, Ct);
        await WaitForReplyAsync(ReplyProcessingStage.Cancelled);
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Null((await final.Applications.SingleAsync(Ct)).Stage);
        Assert.Empty(await final.ApplicationEvents.ToListAsync(Ct));
        Assert.Single(await final.Interviews.ToListAsync(Ct));
        Assert.Equal(InterviewKind.Technical, (await ReadAsync(source.Id)).Kind);
        Assert.Equal(vacancyChanged ? 0 : 1, await final.InterviewPreparationRequests.CountAsync(Ct));
        Assert.Contains("Invitation", (await final.ReplyProcessingRequests.SingleAsync(Ct)).RawJson);
        Assert.Equal(1, provider.Calls);
    }

    private async Task WaitForReplyAsync(ReplyProcessingStage stage)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        while (true)
        {
            await using var context = await pg.Db.CreateDbContextAsync(timeout.Token);
            var root = await context.ReplyProcessingRequests.AsNoTracking().SingleOrDefaultAsync(timeout.Token);
            if (root?.Stage == stage) return;
            await Task.Delay(100, timeout.Token);
        }
    }

    private sealed class InvitationProvider : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls { get; private set; }
        public JsonNode? Body { get; private set; }
        public Func<Task>? BeforeReply { get; set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri?.Host != "provider.invalid") throw new InvalidOperationException("invitation fixture refuses other hosts");
            Calls++;
            Body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (BeforeReply is { } callback) await callback();
            var reply = JsonSerializer.Serialize(new ReplyAnalysis
            {
                Classification = "invitation", Summary = "System design interview", InterviewAt = "2026-10-15T15:00:00+03:00",
                InterviewKind = "system_design", Intent = "scheduled", Counterpart = "human", Draft = null,
            }, CareerJsonContext.Default.ReplyAnalysis);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject
                {
                    ["model"] = "synthetic/reply",
                    ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = reply }, ["finish_reason"] = "stop" }),
                    ["usage"] = new JsonObject { ["prompt_tokens"] = 100, ["completion_tokens"] = 50, ["cost"] = 0.0001 },
                }.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        }
    }
}
