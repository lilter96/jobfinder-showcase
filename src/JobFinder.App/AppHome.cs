namespace JobFinder.App;

/// <summary>Where config/, prompts/, profile/ and data/ live, and how the configuration is layered on top of them.</summary>
public static class AppHome
{
    /// <summary>
    /// <c>JOBFINDER_HOME</c> if set; else the first directory, walking up from the current one and then from the binary, that contains
    /// <c>config/appsettings.json</c>. So <c>dotnet run --project src/JobFinder.App</c> works from anywhere inside the repo.
    /// </summary>
    public static string Find()
    {
        if (Environment.GetEnvironmentVariable("JOBFINDER_HOME") is { Length: > 0 } explicitHome) return Path.GetFullPath(explicitHome);
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "config", "appsettings.json"))) return dir.FullName;
            }
        }

        return Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// config/appsettings.json (placeholders, committed) → config/appsettings.Development.json (real keys, gitignored; the name is kept
    /// from ShadowPrompter's convention, it is loaded in every environment) → config/appsettings.Local.json → environment variables
    /// <c>JOBFINDER_Section__Key</c>. Later wins.
    /// </summary>
    public static void AddConfiguration(IConfigurationBuilder config)
    {
        config.Sources.Clear();
        config.AddEnvironmentVariables("DOTNET_");
        config.AddEnvironmentVariables("ASPNETCORE_");
        config.AddJsonFile("config/appsettings.json", optional: false, reloadOnChange: false);
        config.AddJsonFile("config/appsettings.Development.json", optional: true, reloadOnChange: false);
        config.AddJsonFile("config/appsettings.Local.json", optional: true, reloadOnChange: false);
        config.AddEnvironmentVariables("JOBFINDER_");
    }
}
