using System.Globalization;
using System.Text;
using System.Text.Json;
using JobFinder.Core.Artifacts;
using JobFinder.Core.Text;
using JobFinder.Llm;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Notifications;

namespace JobFinder.Pipeline.Modules.Preparation;

public interface IWeeklyReportSteps
{
    void EnsureCompatible(WeeklyReportInput input);
    Task<WeeklyReportResult> PrepareAsync(WeeklyReportInput input, CancellationToken ct);
}

public sealed class WeeklyReportSteps(JobFinderLlm llm, TimeProvider time) : IWeeklyReportSteps
{
    internal static ArtifactRecipe Recipe(JobFinderLlm operation, WeeklyReportInput input) => operation.Recipe(
        [(LlmTaskCatalog.HhAdvice, LlmPurpose.Resume)], new Dictionary<string, string?>
        {
            ["profile"] = input.ProfileJson, ["resume"] = input.ResumeText,
            ["frame"] = JsonSerializer.Serialize(input.Frame, WeeklyReportJsonContext.Default.WeeklyReportFrame),
            ["rendered-day"] = input.CapturedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        }, new Dictionary<string, string> { ["weekly-report"] = "native-v1" });

    public void EnsureCompatible(WeeklyReportInput input)
    {
        if (Recipe(llm.RestoreOperation(input.Operation), input).Hash != input.Recipe.Hash) throw new WeeklyReportSupersededException();
    }

    public async Task<WeeklyReportResult> PrepareAsync(WeeklyReportInput input, CancellationToken ct)
    {
        EnsureCompatible(input);
        if (!input.NeedsAdvice) return Statistics(input, time.GetUtcNow());
        var result = await llm.RestoreOperation(input.Operation).HhAdviceAsync(input.ResumeText, input.Frame.DemandText, input.Frame.FunnelText, ct);
        return new() { Html = Compose(input.Frame, result.Value), Sent = input.Frame.Sent, Advice = result.Value,
            Model = result.Model, CostUsd = result.CostUsd, GeneratedAt = time.GetUtcNow() };
    }

    internal static WeeklyReportResult Statistics(WeeklyReportInput input, DateTimeOffset at) =>
        new() { Html = Compose(input.Frame, null), Sent = input.Frame.Sent, GeneratedAt = at };

    internal static string Compose(WeeklyReportFrame frame, HhResumeAdvice? advice)
    {
        var text = new StringBuilder(frame.HeaderHtml);
        if (advice is not null) text.Append('\n').Append(AdviceHtml(advice));
        text.Append(frame.CostHtml);
        return CareerCards.Truncate(text.ToString());
    }

    internal static string AdviceHtml(HhResumeAdvice advice)
    {
        var text = new StringBuilder("<b>Резюме на hh</b>\n");
        text.Append("Заголовок: <code>").Append(TextUtil.EscapeHtml(advice.Title)).Append("</code>\n");
        if (advice.SkillsToAdd.Count > 0) text.Append("Добавить в навыки: ").Append(TextUtil.EscapeHtml(string.Join(", ", advice.SkillsToAdd))).Append('\n');
        foreach (var improvement in advice.Improvements.Take(5)) text.Append("• ").Append(TextUtil.EscapeHtml(improvement)).Append('\n');
        if (advice.MarketGaps.Count > 0) text.Append("Подтянуть (не писать в резюме): ").Append(TextUtil.EscapeHtml(string.Join(", ", advice.MarketGaps))).Append('\n');
        text.Append("«О себе» целиком — на странице «Аналитика» дашборда.\n");
        return CareerCards.Truncate(text.ToString());
    }
}
