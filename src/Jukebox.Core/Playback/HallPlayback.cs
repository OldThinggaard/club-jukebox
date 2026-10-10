namespace Jukebox.Core.Playback;

public static class HallPlayback
{
    public static string AudioEnv(string? sink, string? port, int volume) =>
        $"SINK={SafeToken(sink)}\nPORT={SafeToken(port)}\nVOLUME={Math.Clamp(volume, 0, 100)}\n";

    public static string Request(string queueId, string absolutePath, string mediaType, long durationMs, string? artPath) =>
        string.Join('\n', Clean(queueId), Clean(absolutePath), Clean(mediaType), Math.Max(0, durationMs).ToString(), Clean(artPath)) + "\n";

    public static TimeSpan PlaybackCap(long durationMs, string? mediaType = null)
    {
        // A missed end signal used to hold the queue for three hours, so the
        // next song never started. A real length moves on a few seconds after
        // the file. A failed probe comes back as about ten seconds, and that
        // must not cut a film off.
        var known = TimeSpan.FromMilliseconds(Math.Max(0, durationMs));
        var slack = TimeSpan.FromSeconds(8);
        if (string.Equals(mediaType, "image", StringComparison.OrdinalIgnoreCase))
            return known + TimeSpan.FromSeconds(2);
        if (known < TimeSpan.FromSeconds(20))
            return TimeSpan.FromHours(3);
        return known + slack;
    }

    public static void AtomicWrite(string path, string text)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, true);
    }

    public static bool ClockEnded(string clockPath, string queueId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(queueId) || !File.Exists(clockPath))
                return false;
            var lines = File.ReadAllLines(clockPath);
            return lines.Length >= 4 && lines[0].Trim() == queueId && lines[3].Trim() == "1";
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static bool DoneIs(string donePath, string queueId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(queueId) || !File.Exists(donePath))
                return false;
            var line = File.ReadAllText(donePath).Trim();
            return line.Length > 0 && line == queueId;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static bool StampFresh(string path, TimeSpan maxAge)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            return age <= maxAge && age > TimeSpan.FromMinutes(-1);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string Clean(string? value) =>
        (value ?? "").Replace("\r", "").Replace("\n", "");

    private static string SafeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        foreach (var ch in value)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not ('.' or '_' or '-' or ':' or '/'))
                return "";
        }

        return value.Trim();
    }
}
