using JobFinder.Core.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace JobFinder.ServiceDefaults;

/// <summary>
/// The .NET Aspire service defaults: OpenTelemetry for logs, metrics and traces (the app's own <see cref="Telemetry"/> source and meter,
/// ASP.NET Core, HttpClient, Npgsql, the runtime), exported over OTLP when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set (the Aspire dashboard
/// in docker compose), metrics also served on <c>/metrics</c> for Prometheus; health checks on <c>/health</c> and <c>/alive</c>.
/// </summary>
public static class ServiceDefaultsExtensions
{
    public const string HealthPath = "/health";
    public const string AlivePath = "/alive";
    public const string MetricsPath = "/metrics";

    /// <param name="web">A web host: adds the Prometheus exporter and ASP.NET Core instrumentation (the CLI has neither).</param>
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder, bool web = true) where TBuilder : IHostApplicationBuilder
    {
        var serviceName = builder.Configuration["OTEL_SERVICE_NAME"] ?? "jobfinder";
        var otlp = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        builder.Logging.AddOpenTelemetry(o =>
        {
            o.IncludeFormattedMessage = true;
            o.IncludeScopes = true;
        });

        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName, serviceVersion: typeof(ServiceDefaultsExtensions).Assembly.GetName().Version?.ToString()))
            .WithMetrics(m =>
            {
                m.AddMeter(Telemetry.Name).AddHttpClientInstrumentation().AddRuntimeInstrumentation().AddMeter("Npgsql");
                if (web) m.AddAspNetCoreInstrumentation().AddPrometheusExporter();
            })
            .WithTracing(t =>
            {
                // Every trace worth reading starts in the app (a job, a stage, a page): a root client span (the bot's long polling, a proxy
                // CONNECT) is dropped with its children. SQL is not traced (each background poll would be a trace of its own); Npgsql's
                // metrics stay.
                t.SetSampler(new ParentBasedSampler(new AppRootSampler())).AddProcessor(new BlazorNoiseFilter()).AddSource(Telemetry.Name)
                    .AddHttpClientInstrumentation();
                if (web)
                {
                    // The Blazor circuit and the health/metrics probes are noise in the traces; pages and APIs stay.
                    t.AddAspNetCoreInstrumentation(o => o.Filter = ctx =>
                        !ctx.Request.Path.StartsWithSegments(MetricsPath) && !ctx.Request.Path.StartsWithSegments(HealthPath)
                        && !ctx.Request.Path.StartsWithSegments(AlivePath) && !ctx.Request.Path.StartsWithSegments("/_blazor")
                        && !ctx.Request.Path.StartsWithSegments("/_framework"));
                }
            });
        if (otlp) telemetry.UseOtlpExporter();

        builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);
        return builder;
    }

    /// <summary>Roots: the app's own spans and server requests are kept; a client call with no parent is background noise.</summary>
    private sealed class AppRootSampler : Sampler
    {
        private static readonly SamplingResult Keep = new(SamplingDecision.RecordAndSample);
        private static readonly SamplingResult Drop = new(SamplingDecision.Drop);

        // Blazor's SignalR traffic (every render ack, JS interop reply, the hour-long circuit) would bury the jobs; page routes stay.
        public override SamplingResult ShouldSample(in SamplingParameters p) =>
            (p.Kind == System.Diagnostics.ActivityKind.Client && !p.Name.StartsWith("llm ", StringComparison.Ordinal))
            || p.Name.StartsWith("Microsoft.AspNetCore.Components.Server.ComponentHub/", StringComparison.Ordinal)
            || p.Name.StartsWith("Circuit ", StringComparison.Ordinal)
                ? Drop
                : Keep;
    }

    /// <summary>
    /// Blazor's SignalR spans get their final name after they start (the sampler sees another one): they are unmarked for export when they
    /// end, before the exporter's processor sees them.
    /// </summary>
    private sealed class BlazorNoiseFilter : BaseProcessor<System.Diagnostics.Activity>
    {
        public override void OnEnd(System.Diagnostics.Activity a)
        {
            if (!a.DisplayName.StartsWith("Microsoft.AspNetCore.Components.Server.ComponentHub/", StringComparison.Ordinal)
                && !a.DisplayName.StartsWith("Circuit ", StringComparison.Ordinal)) return;
            a.ActivityTraceFlags &= ~System.Diagnostics.ActivityTraceFlags.Recorded;
        }
    }

    /// <summary>The web host's endpoints: Prometheus scraping, readiness and liveness.</summary>
    public static WebApplication MapServiceDefaults(this WebApplication app)
    {
        app.MapPrometheusScrapingEndpoint(MetricsPath);
        app.MapHealthChecks(HealthPath);
        app.MapHealthChecks(AlivePath, new HealthCheckOptions { Predicate = r => r.Tags.Contains("live") });
        return app;
    }
}
