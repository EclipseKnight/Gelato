namespace Gelato.Services;

/// <summary>
/// When the weekly pass of the tree sync last finished, kept in a small file of its own rather
/// than by rewriting the whole plugin configuration from a background task.
/// </summary>
public static class FullPassState
{
    private const string FileName = "last-full-series-sync.txt";

    /// <summary>The saved time, or <paramref name="fallback"/> (the old config value) when none is saved.</summary>
    public static DateTime? Read(string folder, DateTime? fallback)
    {
        try
        {
            var text = File.ReadAllText(Path.Combine(folder, FileName)).Trim();
            if (DateTime.TryParse(text, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t))
                return t;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return fallback;
    }

    public static void Write(string folder, DateTime utc)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, FileName), utc.ToString("O"));
    }
}
