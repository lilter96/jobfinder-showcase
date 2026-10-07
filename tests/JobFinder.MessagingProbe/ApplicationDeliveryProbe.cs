using System.Data.Common;
using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Modules.Hiring;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Wolverine;

namespace JobFinder.MessagingProbe;

public static class ApplicationDeliveryProbe
{
    public static string CounterKey(Guid id) => $"probe.application-effect.{id:N}";

    public static async Task<int> RunAsync(string[] args)
    {
        if (!Guid.TryParseExact(args[1], "N", out var id) || args[2] is not ("ready" or "claim" or "effect" or "receipt")) return 2;
        var connectionString = Environment.GetEnvironmentVariable("JOBFINDER_DISPOSABLE_TEST_DATABASE");
        if (string.IsNullOrWhiteSpace(connectionString)) return 2;
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        if (connection.Host is not ("127.0.0.1" or "localhost") || connection.Database != "postgres" || connection.Port is 5432 or 5433) return 2;
        try
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            builder.Logging.ClearProviders();
            builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json"));
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = connectionString,
                ["Paths:Prompts"] = Path.Combine(AppContext.BaseDirectory, "prompts"),
                ["Paths:Data"] = Path.Combine(OperatingSystem.IsWindows() ? @"E:\Temp" : Path.GetTempPath(), "jobfinder-application-probe"),
                ["Llm:ApiKey"] = "synthetic-disposable-probe-key",
                ["HeadHunter:Enabled"] = "false", ["Habr:Enabled"] = "false", ["Threads:Enabled"] = "false",
            });
            builder.Services.AddJobFinderCore(builder.Configuration);
            builder.Services.RemoveAll<IDbContextFactory<JobFinderDbContext>>();
            builder.Services.AddDbContextFactory<JobFinderDbContext>(options =>
            {
                JobFinderDbContext.Configure(options, connectionString);
                if (args[2] != "effect") options.AddInterceptors(new PauseAfterCommit(id, args[2]));
            });
            builder.Services.RemoveAll<IApplicationDeliveryGate>();
            builder.Services.AddSingleton<IApplicationDeliveryGate, Gate>();
            builder.Services.RemoveAll<IApplicationDeliveryControls>();
            builder.Services.AddSingleton<IApplicationDeliveryControls, Controls>();
            builder.Services.RemoveAll<IDeliveryTransport>();
            builder.Services.AddSingleton<IDeliveryTransport>(provider => new Recipient(
                provider.GetRequiredService<IDbContextFactory<JobFinderDbContext>>(), id, args[2]));
            builder.Services.RemoveAll<IHttpClientFactory>();
            builder.Services.AddSingleton<IHttpClientFactory, NoNetwork>();
            builder.AddJobFinderMessaging(consumer: true);
            builder.Services.ConfigureWolverine(ResumeProbe.FastRecovery);
            builder.Services.AddJobFinderInitialization();
            using var host = builder.Build();
            await host.StartAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.ToString().Replace(connectionString, "<test database>").Replace(connection.Password ?? "<unset>", "<redacted>"));
            return 1;
        }
    }

    private sealed class PauseAfterCommit(Guid id, string boundary) : DbTransactionInterceptor
    {
        private int _armed = 1;
        private readonly ApplicationDeliveryStage _stage = boundary switch
        {
            "ready" => ApplicationDeliveryStage.Ready,
            "claim" => ApplicationDeliveryStage.Sending,
            "receipt" => ApplicationDeliveryStage.Completed,
            _ => throw new ArgumentException("unsupported checkpoint", nameof(boundary)),
        };

        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<ApplicationDeliveryRequest>().Any(entry => entry.Entity.Id == id && entry.Entity.Stage == _stage)
                && Interlocked.Exchange(ref _armed, 0) == 1)
                await PauseAsync(ct);
        }
    }

    private static async Task PauseAsync(CancellationToken ct)
    {
        Console.WriteLine("READY");
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    private sealed class Gate : IApplicationDeliveryGate
    {
        public Task<EmployerMessagePreparation> PrepareAsync(ApplicationDeliveryRequest request, ApplicationDeliveryInput input, Application application, CancellationToken ct) =>
            Task.FromResult<EmployerMessagePreparation>(new EmployerMessagePreparation.Ready(Packet(request, input, application)));

        public Task<ApprovedDispatch> RestoreAsync(ApplicationDeliveryRequest request, ApplicationDeliveryInput input, Application application, CancellationToken ct) =>
            Task.FromResult(Packet(request, input, application));

        private static ApprovedDispatch Packet(ApplicationDeliveryRequest request, ApplicationDeliveryInput input, Application application)
        {
            if (input.Channel != ApplicationChannel.HeadHunter) throw new InvalidOperationException("probe accepts synthetic hh without an attachment only");
            return new ApprovedDispatch(application, input.Text, request.Review ?? new ApplicationLetterReview
            {
                Score = 10, TextHash = Fingerprint.Of(input.Text), ContextHash = input.ReviewContextHash, Recipe = input.Recipe,
            }, null, null, false, ApplicationDeliveryRequestId: request.Id);
        }
    }

    private sealed class Controls : IApplicationDeliveryControls
    {
        public Task<ApplicationDeliveryControl> ReadAsync(ApplicationDeliveryInput input, CancellationToken ct) => Task.FromResult(new ApplicationDeliveryControl());
    }

    private sealed class Recipient(IDbContextFactory<JobFinderDbContext> db, Guid id, string boundary) : IDeliveryTransport
    {
        public async Task<DeliveryReceipt> SendAsync(ApprovedDispatch dispatch, CancellationToken ct)
        {
            if (dispatch.ApplicationDeliveryRequestId != id) throw new InvalidOperationException("probe received a different application");
            await using var context = await db.CreateDbContextAsync(ct);
            var key = CounterKey(id);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO settings (key, value, updated_at) VALUES ({key}, '1', now())
                ON CONFLICT (key) DO UPDATE SET value = (settings.value::int + 1)::text, updated_at = now()
                """, ct);
            if (boundary == "effect") await PauseAsync(ct);
            return new DeliveryReceipt(ApplicationStatus.Sent);
        }
    }

    private sealed class NoNetwork : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("application crash probe must not use a real network");
    }
}
