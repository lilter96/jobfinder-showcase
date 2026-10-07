using JobFinder.App;
using JobFinder.Llm;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Configuration;
using JobFinder.Pipeline.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace JobFinder.Tests.Pipeline;

public sealed class DeploymentConfigurationTests
{
    [Fact]
    public void Service_aliases_resolve_the_same_validated_native_options_instance()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = "Host=localhost;Database=test;Username=test;Password=test",
            ["Llm:TimeoutSec"] = "37",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddJobFinderCore(configuration);
        using var provider = services.BuildServiceProvider();
        Assert.Same(provider.GetRequiredService<DatabaseOptions>(), provider.GetRequiredService<IOptions<DatabaseOptions>>().Value);
        Assert.Same(provider.GetRequiredService<LlmOptions>(), provider.GetRequiredService<IOptions<LlmOptions>>().Value);
        Assert.Equal(37, provider.GetRequiredService<LlmOptions>().TimeoutSec);
    }

    [Fact]
    public void Cli_validation_runs_without_starting_a_host_or_any_background_service()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Llm:TimeoutSec"] = "0" }).Build();
        var services = new ServiceCollection();
        DeploymentConfiguration.Add<LlmOptions>(services, configuration, LlmOptions.Section);
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => DeploymentConfiguration.Validate(provider, LlmOptions.Section));
    }

    [Fact]
    public void Obsolete_bootstrap_preferences_are_not_a_second_deployment_policy_owner()
    {
        var options = new LlmOptions
        {
            DailyBudgetUsd = -1, MaxAttempts = -1,
            Routes = new() { ["outdated-bootstrap-purpose"] = [new() { Name = "unused", ExtraBody = "invalid" }] },
        };
        Assert.True(new DeploymentOptionsValidator<LlmOptions>().Validate(null, options).Succeeded);
        Assert.Throws<ArgumentException>(() => JobFinder.Llm.Configuration.LlmRuntimePolicyCompiler.Import(options));
    }

    [Fact]
    public void Ownership_migration_and_policy_validation_precede_other_background_services()
    {
        var services = new ServiceCollection();
        services.AddJobFinderPipeline();
        var hosted = services.Where(service => service.ServiceType == typeof(IHostedService)).Take(7).ToArray();
        Assert.Equal(typeof(SingleInstanceGuard), hosted[0].ImplementationType);
        Assert.Equal(typeof(DatabaseInitializer), hosted[1].ImplementationType);
        var publisher = new NativeBootstrapPublisher(new NativePublicationScopes(null!), null!, new DatabaseOptions(), new ServerOwnership());
        using var provider = new ServiceCollection().AddSingleton(publisher).BuildServiceProvider();
        Assert.NotNull(hosted[2].ImplementationFactory);
        Assert.Same(publisher, hosted[2].ImplementationFactory!(provider));
        Assert.Equal(typeof(StartupRecovery), hosted[3].ImplementationType);
        Assert.Equal(typeof(JobFinder.Pipeline.Settings.PolicyInitializer), hosted[4].ImplementationType);
        Assert.NotNull(hosted[5].ImplementationFactory);
        Assert.Equal(typeof(NativeBootstrapCompletion), hosted[6].ImplementationType);
    }

    private sealed class StartedService : IHostedService
    {
        public bool Started { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken) { Started = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task Invalid_options_stop_server_startup_before_background_effects()
    {
        var service = new StartedService();
        using var host = new HostBuilder().ConfigureServices(services =>
        {
            DeploymentConfiguration.Add<DatabaseOptions>(services, new ConfigurationBuilder().Build(), DatabaseOptions.Section);
            services.AddSingleton<IHostedService>(service);
        }).Build();
        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
        Assert.False(service.Started);
    }

    [Fact]
    public void Server_and_cli_share_the_same_configuration_sources_and_precedence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "jobfinder-config-" + Guid.NewGuid().ToString("N"));
        var configDirectory = Path.Combine(directory, "config");
        Directory.CreateDirectory(configDirectory);
        var baseline = Path.Combine(configDirectory, "appsettings.json");
        var development = Path.Combine(configDirectory, "appsettings.Development.json");
        var local = Path.Combine(configDirectory, "appsettings.Local.json");
        try
        {
            File.WriteAllText(baseline, "{\"Llm\":{\"TimeoutSec\":10}}", new System.Text.UTF8Encoding(false));
            File.WriteAllText(development, "{\"Llm\":{\"TimeoutSec\":20}}", new System.Text.UTF8Encoding(false));
            File.WriteAllText(local, "{\"Llm\":{\"TimeoutSec\":30}}", new System.Text.UTF8Encoding(false));
            var server = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = directory, Args = [] });
            var cli = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = directory, Args = [] });
            server.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["stray.root"] = "server-only" });
            AppHome.AddConfiguration(server.Configuration);
            AppHome.AddConfiguration(cli.Configuration);
            Assert.Null(server.Configuration["stray.root"]);
            Assert.Equal("30", server.Configuration["Llm:TimeoutSec"]);
            Assert.Equal(server.Configuration["Llm:TimeoutSec"], cli.Configuration["Llm:TimeoutSec"]);
            Assert.Equal(server.Configuration.Sources.Select(source => source.GetType()), cli.Configuration.Sources.Select(source => source.GetType()));
        }
        finally
        {
            File.Delete(baseline);
            File.Delete(development);
            File.Delete(local);
            Directory.Delete(configDirectory);
            Directory.Delete(directory);
        }
    }
}
