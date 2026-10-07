using JobFinder.Core.Options;
using JobFinder.Llm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace JobFinder.Pipeline.Configuration;

public sealed record DeploymentSection(string Name, Action<IServiceProvider> Validate);

public static class DeploymentConfiguration
{
    public static void Add<T>(IServiceCollection services, IConfiguration configuration, string section) where T : class, new()
    {
        services.AddSingleton<IValidateOptions<T>>(new DeploymentOptionsValidator<T>());
        services.AddOptions<T>().Bind(configuration.GetSection(section)).ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<T>>().Value);
        services.AddSingleton(new DeploymentSection(section, provider => _ = provider.GetRequiredService<IOptions<T>>().Value));
    }

    public static void Validate(IServiceProvider services, params string[] sections)
    {
        foreach (var section in services.GetServices<DeploymentSection>().Where(section => sections.Contains(section.Name, StringComparer.OrdinalIgnoreCase)))
            section.Validate(services);
    }
}

public sealed class DeploymentOptionsValidator<T> : IValidateOptions<T> where T : class
{
    public ValidateOptionsResult Validate(string? name, T options)
    {
        var problem = options switch
        {
            DatabaseOptions database => DatabaseProblem(database),
            PathsOptions paths => string.IsNullOrWhiteSpace(paths.Prompts) ? "Paths:Prompts is required" : null,
            LlmOptions llm => LlmProblem(llm),
            DecisionOptions decision => decision.AutoApplyThreshold is < 0 or > 101 || decision.ApprovalThreshold is < 0 or > 100
                || decision.DailyAutoApplyLimit is < 0 or > 200 ? "Decision thresholds or daily limit are out of range" : null,
            _ => null,
        };
        return problem is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problem);
    }

    private static string? DatabaseProblem(DatabaseOptions options)
    {
        try
        {
            var connection = new Npgsql.NpgsqlConnectionStringBuilder(options.ConnectionString);
            return string.IsNullOrWhiteSpace(connection.Host) || string.IsNullOrWhiteSpace(connection.Database)
                ? "Database:ConnectionString requires a host and database" : null;
        }
        catch (ArgumentException)
        {
            return "Database:ConnectionString is not a valid PostgreSQL connection string";
        }
    }

    private static string? LlmProblem(LlmOptions options)
    {
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("https" or "http"))
            return "Llm:Endpoint must be an absolute HTTP URL";
        return options.TimeoutSec <= 0 ? "Llm transport timeout is invalid" : null;
    }
}
