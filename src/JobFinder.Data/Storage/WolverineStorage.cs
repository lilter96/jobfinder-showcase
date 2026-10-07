namespace JobFinder.Data.Storage;

public static class WolverineStorage
{
    public const string PackageVersion = "6.41.0";
    public const string MessageSchema = "jobfinder_messages";
    public const string QueueSchema = "jobfinder_queues";

    public static string LetterQueuesSql { get; } = ReadScript("Wolverine.LetterQueues.6.41.0.sql");
    public static string ReplyQueueSql { get; } = ReadScript("Wolverine.ReplyQueue.6.41.0.sql");
    public static string BotControlsQueueSql { get; } = ReadScript("Wolverine.BotControlsQueue.6.41.0.sql");

    public static string CreateSql { get; } = ReadScript("Wolverine.6.41.0.sql") + Environment.NewLine + LetterQueuesSql + Environment.NewLine + ReplyQueueSql + Environment.NewLine + BotControlsQueueSql;

    private static string ReadScript(string scriptName)
    {
        using var stream = typeof(WolverineStorage).Assembly.GetManifestResourceStream($"JobFinder.Data.Storage.Scripts.{scriptName}")
            ?? throw new InvalidOperationException($"The pinned Wolverine storage script '{scriptName}' is missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
