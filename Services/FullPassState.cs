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
            if (DateTime.TryParseExact(text, "O", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var t))
                return t;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return fallback;
    }

    /// <summary>
    /// Saves the time (temp file, then move). False when the folder can't be written: the tree
    /// sync goes on, and the weekly pass simply counts as not done yet.
    /// </summary>
    public static bool Write(string folder, DateTime utc)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, FileName);
            File.WriteAllText(file + ".tmp", utc.ToString("O"));
            File.Move(file + ".tmp", file, true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
