using System.Text.Json;

namespace JobFinder.Tests.Llm;

public sealed class ApplicationRouteTests
{
    [Theory]
    [InlineData("Application", 24000)]
    [InlineData("Resume", 32000)]
    public void Every_quality_route_model_keeps_maximum_reasoning_and_enough_output_budget(string purpose, int minimumTokens)
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json")));
        var models = settings.RootElement.GetProperty("Llm").GetProperty("Routes").GetProperty(purpose).EnumerateArray();
        foreach (var model in models)
        {
            using var extra = JsonDocument.Parse(model.GetProperty("ExtraBody").GetString()!);
            var reasoning = extra.RootElement.GetProperty("reasoning");
            Assert.Contains(reasoning.GetProperty("effort").GetString(), new[] { "max", "xhigh" });
            Assert.False(reasoning.TryGetProperty("enabled", out var enabled) && !enabled.GetBoolean());
            Assert.True(model.GetProperty("MaxTokens").GetInt32() >= minimumTokens, model.GetProperty("Name").GetString());
        }
    }
}
