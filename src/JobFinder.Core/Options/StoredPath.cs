namespace JobFinder.Core.Options;

/// <summary>
/// File paths kept in the database (tailored resumes, interview packs, bot attachments). The host (E:\Work\jobfinder) and the container
/// (/app) see the same <c>data/</c> folder under different roots, so paths are stored relative to the JobFinder home (the current
/// directory of both) and resolved on use. Absolute paths written before that are re-rooted at their <c>data</c> segment.
/// </summary>
public static class StoredPath
{
    public static string ToStored(string fullPath)
    {
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), fullPath);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? fullPath : relative.Replace('\\', '/');
    }

    public static string Resolve(string stored)
    {
        if (!Path.IsPathRooted(stored) && !stored.Contains(":\\", StringComparison.Ordinal)) return Path.GetFullPath(stored);
        if (File.Exists(stored)) return stored;
        var normalized = stored.Replace('\\', '/');
        var i = normalized.IndexOf("/data/", StringComparison.Ordinal);
        return i >= 0 ? Path.GetFullPath(normalized[(i + 1)..]) : stored;
    }

    public static bool Exists(string? stored) => stored is not null && File.Exists(Resolve(stored));
}
