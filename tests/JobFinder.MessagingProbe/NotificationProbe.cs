using System.Data.Common;
using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Wolverine;

namespace JobFinder.MessagingProbe;

public static class NotificationProbe
{
    public const string Marker = "Synthetic notification process probe";

    public static async Task<int> RunAsync(string[] args)
    {
        if (!Guid.TryParseExact(args[1], "N", out var id) || args[2] is not ("before-claim-commit" or "after-claim-commit"
            or "before-effect" or "after-effect" or "before-receipt-commit" or "after-receipt-commit")) return 2;
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
                ["Database:ConnectionString"] = connectionString, ["Paths:Prompts"] = Path.Combine(AppContext.BaseDirectory, "prompts"),
                ["Paths:Data"] = Path.Combine(OperatingSystem.IsWindows() ? @"E:\Temp" : Path.GetTempPath(), "jobfinder-notification-probe", id.ToString("N")),
                ["Paths:Profile"] = Path.Combine(OperatingSystem.IsWindows() ? @"E:\Temp" : Path.GetTempPath(), "jobfinder-notification-probe", id.ToString("N"), "probe-profile"),
                ["Llm:ApiKey"] = "synthetic-notification-probe-key", ["Llm:Proxy"] = "", ["Llm:Endpoint"] = "https://provider.invalid/completions",
            });
            builder.Services.AddJobFinderCore(builder.Configuration);
            builder.Services.RemoveAll<IDbContextFactory<JobFinderDbContext>>();
            builder.Services.AddDbContextFactory<JobFinderDbContext>(options =>
            {
                JobFinderDbContext.Configure(options, connectionString);
                options.AddInterceptors(new Boundary(id, args[2]));
            });
            builder.Services.AddSingleton<INotificationDeliveryTransport>(provider => new NotificationProbeTransport(
                provider.GetRequiredService<IDbContextFactory<JobFinderDbContext>>(), id, args[2]));
            builder.Services.RemoveAll<IHttpClientFactory>();
            builder.Services.AddSingleton<IHttpClientFactory>(new NoNetwork());
            builder.AddJobFinderMessaging(consumer: true);
            builder.Services.ConfigureWolverine(ResumeProbe.FastRecovery);
            builder.Services.AddJobFinderInitialization();
            using var host = builder.Build();
            await using (var context = await host.Services.GetRequiredService<IDbContextFactory<JobFinderDbContext>>().CreateDbContextAsync())
            {
                var root = await context.NotificationDeliveryRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id);
                if (root is null || !await context.Notifications.AnyAsync(row => row.Id == root.NotificationId && row.Text == Marker)) return 2;
            }
            await host.StartAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.GetType().Name + ": " + error.Message); return 1; }
    }

    private sealed class Boundary(Guid id, string boundary) : DbTransactionInterceptor
    {
        private int _armed = 1;
        private Task PauseAsync(DbContext context, CancellationToken ct)
        {
            var stage = boundary.Contains("claim", StringComparison.Ordinal) ? NotificationDeliveryStage.Sending : NotificationDeliveryStage.Completed;
            return context.ChangeTracker.Entries<NotificationDeliveryRequest>().Any(entry => entry.Entity.Id == id && entry.Entity.Stage == stage)
                && Interlocked.Exchange(ref _armed, 0) == 1 ? CommentDeliveryProbeTransport.PauseAsync(ct) : Task.CompletedTask;
        }

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken ct = default)
        {
            if (boundary is "before-claim-commit" or "before-receipt-commit") await PauseAsync(eventData.Context!, ct);
            return result;
        }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default) =>
            boundary is "after-claim-commit" or "after-receipt-commit" ? PauseAsync(eventData.Context!, ct) : Task.CompletedTask;
    }

    private sealed class NoNetwork : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Refuse());
        private sealed class Refuse : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                throw new InvalidOperationException("notification process probe refuses external HTTP");
        }
    }
}

public sealed class NotificationProbeTransport(IDbContextFactory<JobFinderDbContext> db, Guid id, string? boundary = null) : INotificationDeliveryTransport
{
    public static string CounterKey(Guid requestId) => "probe.notification." + requestId.ToString("N");
    public NotificationTarget CaptureTarget() => new() { ChatId = 123456, BotId = 123456 };

    public async Task<NotificationEffectReceipt> SendAsync(NotificationTarget target, NotificationEffect effect, byte[]? attachment,
        Func<CancellationToken, Task<bool>> maySend, CancellationToken ct)
    {
        if (target != CaptureTarget() || effect.Kind != NotificationEffectKind.Text || effect.Text != NotificationProbe.Marker || !await maySend(ct))
            return new() { Status = DeliveryAttemptStatus.KnownNotSent, Error = "synthetic notification permission refused" };
        if (boundary == "before-effect") await CommentDeliveryProbeTransport.PauseAsync(ct);
        await using var context = await db.CreateDbContextAsync(ct);
        var key = CounterKey(id);
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO settings (key, value, updated_at) VALUES ({key}, '1', now()) ON CONFLICT (key) DO UPDATE SET value = (settings.value::int + 1)::text, updated_at = now()", ct);
        if (boundary == "after-effect") await CommentDeliveryProbeTransport.PauseAsync(ct);
        return new() { Status = DeliveryAttemptStatus.Confirmed, MessageId = 73 };
    }
}
