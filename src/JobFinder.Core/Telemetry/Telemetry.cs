using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace JobFinder.Core.Telemetry;

/// <summary>
/// The app's own traces and metrics, through System.Diagnostics (OpenTelemetry's API in .NET): every layer starts spans and records
/// measurements here, <c>JobFinder.ServiceDefaults</c> exports them (OTLP to the Aspire dashboard, <c>/metrics</c> for Prometheus).
/// Spans: a job run, a worker item, each LLM attempt, each stage of the interview preparation and the HR document. Metrics: LLM calls,
/// latency, tokens and cost by purpose and model; job runs and durations; stage durations; items processed by workers.
/// </summary>
public static class Telemetry
{
    public const string Name = "JobFinder";

    public static readonly ActivitySource Source = new(Name);

    public static readonly Meter Meter = new(Name);

    public static readonly Counter<long> LlmCalls = Meter.CreateCounter<long>("jobfinder.llm.calls", description: "LLM attempts by purpose, model and outcome");

    public static readonly Histogram<double> LlmDuration = Meter.CreateHistogram<double>("jobfinder.llm.duration", "s", "LLM attempt latency");

    public static readonly Counter<long> LlmTokens = Meter.CreateCounter<long>("jobfinder.llm.tokens", description: "LLM tokens by purpose, model and kind");

    public static readonly Counter<double> LlmCost = Meter.CreateCounter<double>("jobfinder.llm.cost", "USD", "LLM spend by purpose and model");

    public static readonly Counter<long> JobRuns = Meter.CreateCounter<long>("jobfinder.job.runs", description: "Scheduled job runs by job and status");

    public static readonly Histogram<double> JobDuration = Meter.CreateHistogram<double>("jobfinder.job.duration", "s", "Scheduled job run duration");

    public static readonly Counter<long> WorkerItems = Meter.CreateCounter<long>("jobfinder.worker.items", description: "Items handled by queue workers");

    public static readonly Counter<long> Answers = Meter.CreateCounter<long>("jobfinder.answers",
        description: "Prepared answers by scope and source: memory / store (reused) or generated");

    public static readonly Counter<long> ScrapeRequests = Meter.CreateCounter<long>("jobfinder.scrape.requests",
        description: "Page requests to scraped sites by family, priority and outcome: ok, blocked, not_found; paused / budget = refused locally");

    public static readonly Histogram<double> StageDuration = Meter.CreateHistogram<double>("jobfinder.stage.duration", "s",
        "Stages of the interview preparation and the HR document");

    /// <summary>A span; null when nobody listens (no exporter), so callers use <c>?.</c>.</summary>
    public static Activity? Start(string name, ActivityKind kind = ActivityKind.Internal) => Source.StartActivity(name, kind);

    /// <summary>A stage span that also records its duration histogram when disposed.</summary>
    public static Stage StartStage(string stage, string scope) => new(stage, scope, Start($"{scope} {stage}"));

    public sealed class Stage(string stage, string scope, Activity? activity) : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();

        public Activity? Activity { get; } = activity;

        public Stage Tag(string key, object? value)
        {
            Activity?.SetTag(key, value);
            return this;
        }

        public void Fail(Exception e) => Activity?.SetStatus(ActivityStatusCode.Error, e.Message);

        public void Dispose()
        {
            StageDuration.Record(Stopwatch.GetElapsedTime(_started).TotalSeconds,
                new KeyValuePair<string, object?>("scope", scope), new KeyValuePair<string, object?>("stage", stage));
            Activity?.Dispose();
        }
    }
}
