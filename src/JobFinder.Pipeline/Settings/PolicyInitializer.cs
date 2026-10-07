using Microsoft.Extensions.Hosting;

namespace JobFinder.Pipeline.Settings;

public sealed class PolicyInitializer(LlmPolicyStore llm, RuntimeSettings runtime, Modules.Discovery.SearchPolicyStore search,
    Modules.Engagement.EngagementPolicyStore engagement) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await runtime.Automation.PreviewAsync(ct);
        await llm.PreviewAsync(ct);
        await search.PreviewAsync(ct);
        await engagement.PreviewAsync(ct);
        await runtime.Automation.ReadAsync(ct);
        await llm.ReadAsync(ct);
        await search.ReadAsync(ct);
        await engagement.ReadAsync(ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
