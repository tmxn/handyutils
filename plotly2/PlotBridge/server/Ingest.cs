namespace PlotBridge.Server;

/// <summary>
/// The file-to-destination convention, shared by the drop folder watcher and the
/// synchronous /ingest endpoint so there is one definition of it.
/// </summary>
public static class Ingest
{
    public readonly record struct Target(string Board, string Chart, string Series);

    /// <summary>
    /// Filename decides the destination, split on "__":
    /// <code>
    /// pts.tsv               -> board "default", chart "main",  series "pts"
    /// hull__pts.tsv         -> board "default", chart "hull",  series "pts"
    /// run2__hull__pts.tsv   -> board "run2",    chart "hull",  series "pts"
    /// </code>
    /// </summary>
    public static Target FromFileName(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var parts = stem.Split("__", StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            >= 3 => new Target(parts[0], parts[1], string.Join("__", parts.Skip(2))),
            2 => new Target("default", parts[0], parts[1]),
            _ => new Target("default", "main", stem),
        };
    }

    /// <summary>
    /// The writer may still hold the file, so retry briefly before giving up.
    /// Returns null if it never became readable, which the caller reports rather
    /// than silently treating as empty.
    /// </summary>
    public static async Task<string?> ReadWhenReadableAsync(string path, int attempts = 12, int delayMs = 60)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                return await sr.ReadToEndAsync();
            }
            catch (IOException)
            {
                await Task.Delay(delayMs);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(delayMs);
            }
        }
        return null;
    }
}
