using JobFinder.Core.Telemetry;
using JobFinder.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JobFinder.Pipeline.Messaging;

public sealed class NativeBootstrapPublisher(NativePublicationScopes publication, IDbContextFactory<JobFinderDbContext> db,
    DatabaseOptions database, ServerOwnership ownership) : IHostedService, IAsyncDisposable
{
    private IHost? _producer;
    private IServiceScopeFactory? _scopes;

    public async Task StartAsync(CancellationToken ct)
    {
        ownership.EnsureOwned();
        if (_producer is not null) throw new InvalidOperationException("bootstrap publisher already started");
        using var stage = Telemetry.StartStage("start-producer", "bootstrap");
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = database.ConnectionString,
        });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(db);
        builder.AddJobFinderMessaging(consumer: false);
        var producer = builder.Build();
        try
        {
            await producer.StartAsync(ct);
            ownership.EnsureOwned();
            var scopes = producer.Services.GetRequiredService<IServiceScopeFactory>();
            publication.Begin(scopes);
            _scopes = scopes;
            _producer = producer;
        }
        catch
        {
            try { await producer.StopAsync(CancellationToken.None); }
            finally { producer.Dispose(); }
            throw;
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        var producer = Interlocked.Exchange(ref _producer, null);
        if (producer is null) return;
        using var stage = Telemetry.StartStage("stop-producer", "bootstrap");
        var scopes = Interlocked.Exchange(ref _scopes, null)!;
        publication.End(scopes);
        try { await producer.StopAsync(ct); }
        finally { producer.Dispose(); }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None);
}

public sealed class NativeBootstrapCompletion(NativeBootstrapPublisher publisher) : IHostedService
{
    public Task StartAsync(CancellationToken ct) => publisher.StopAsync(ct);
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
