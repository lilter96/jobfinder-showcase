using JasperFx;
using JobFinder.Core.Messaging;
using JobFinder.Data.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Weasel.Core.Migrations;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.Runtime;

namespace JobFinder.MessagingProbe;

public static class StorageScript
{
    public static async Task<string> ExportAsync()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.UseWolverine(options =>
        {
            options.UsePostgresqlPersistenceAndTransport("Host=127.0.0.1;Port=1;Database=offline_schema_export;Username=unused;Password=unused", WolverineStorage.MessageSchema, WolverineStorage.QueueSchema);
            options.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            options.Discovery.DisableConventionalDiscovery();
            options.Durability.DurabilityAgentEnabled = false;
            foreach (var queue in DurableQueues.All)
                options.ListenToPostgresqlQueue(queue);
        });
        using var host = builder.Build();
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<PostgresqlTransport>();
        await transport.ConfigureAsync(runtime);
        return ((IDatabase)runtime.Storage).ToDatabaseScript();
    }
}
