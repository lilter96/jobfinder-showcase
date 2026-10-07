using JobFinder.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Bot;

public static class BotServices
{
    public static IServiceCollection AddJobFinderBot(this IServiceCollection services, IConfiguration config, bool withLoops)
    {
        var options = config.GetSection(BotOptions.Section).Get<BotOptions>() ?? new BotOptions();
        JobFinder.Pipeline.Configuration.DeploymentConfiguration.Add<BotOptions>(services, config, BotOptions.Section);
        services.AddHttpClient(TelegramBotClientHolder.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(90))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                KeepAlivePingDelay = TimeSpan.FromSeconds(15),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            });
        services.AddSingleton<TelegramBotClientHolder>();
        services.AddSingleton<JobFinder.Pipeline.Notifications.INotificationDeliveryTransport, NotificationDeliveryTransport>();
        services.AddSingleton<CareerBot>();
        services.AddSingleton<OwnerBotMessages>();
        services.AddSingleton<BotService>();
        services.AddSingleton<JobFinder.Pipeline.Controls.IBotControlExecutor>(provider => provider.GetRequiredService<BotService>());
        if (withLoops)
        {
            if (options.Enabled) services.AddHostedService(provider => provider.GetRequiredService<BotService>());
            services.AddHostedService<NotificationDispatcher>();
        }

        return services;
    }
}
