namespace JobFinder.Tests;

internal static class Fixture
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static string PromptsDir => Path.Combine(AppContext.BaseDirectory, "prompts");
}
