namespace Jukebox.Core.Playback;

public static class HallPlayback
{
    public static string AudioEnv(string? sink, string? port, int volume) =>
        $"SINK={SafeToken(sink)}\nPORT={SafeToken(port)}\nVOLUME={Math.Clamp(volume, 0, 100)}\n";

    /// <summary>
    /// Volume for the player. The sink is left blank on purpose: looking it up
    /// from the service calls pactl, and that call was blocking the queue and
    /// the volume buttons. The player finds the analog output itself.
    /// </summary>
    public static string RouteFile(string? audioDevice, int volumePercent)
    {
        var port = DisplayAudioProbe.WantsHeadphones(audioDevice)
            ? "analog-output-headphones"
            : "analog-output-speaker";
        return AudioEnv(null, port, volumePercent);
    }

    public const string WakeId = "wake";
    public const string WakeFile = "/opt/jukebox/lib/wake.mp4";

    public static string StopRequest() => Request("stop", "", "", 0, null);

    public static string WakeRequest() => Request(WakeId, WakeFile, "video", 3_000, null);

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

    /// <summary>
    /// An end signal in the first seconds is mpv opening the file, not the
    /// song finishing. Acting on it starts the same song again.
    /// </summary>
    public static bool EndIsBelievable(long elapsedMs) => elapsedMs >= 5_000;

    /// <summary>
    /// The welcome screen must stay away while the file still has time left.
    /// A short reading from mpv is not the length of the song.
    /// </summary>
    public static bool StillInTheMiddle(long positionMs, long durationMs) =>
        durationMs >= 20_000 && positionMs >= 0 && positionMs + 8_000 < durationMs;

    /// <summary>
    /// A song that has just started must not be started again. That loop
    /// never reaches the next song in the queue.
    /// </summary>
    public static bool SameSongTooSoon(string? previousMediaId, string mediaId, long sinceMs) =>
        !string.IsNullOrEmpty(previousMediaId)
        && string.Equals(previousMediaId, mediaId, StringComparison.Ordinal)
        && sinceMs >= 0
        && sinceMs < 180_000;

    /// <summary>
    /// The file is being opened again and again, so the queue must move on.
    /// A missing clock is -1 and must not count: the song may still be playing.
    /// </summary>
    public static bool StuckBeforeItPlays(long elapsedMs, long maxPositionMs) =>
        maxPositionMs >= 0 && maxPositionMs < 2_000 && elapsedMs >= 45_000;

    public static long ClockPositionMs(string clockPath, string queueId)
    {
        try
        {
            var lines = ReadClock(clockPath, queueId);
            if (lines is null || lines.Length < 2)
                return -1;
            return long.TryParse(lines[1].Trim(), out var ms) && ms >= 0 ? ms : -1;
        }
        catch (IOException)
        {
            return -1;
        }
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
            var lines = ReadClock(clockPath, queueId);
            return lines is not null && lines.Length >= 4 && lines[3].Trim() == "1";
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static long ClockDurationMs(string clockPath, string queueId)
    {
        try
        {
            var lines = ReadClock(clockPath, queueId);
            if (lines is null || lines.Length < 3)
                return 0;
            return long.TryParse(lines[2].Trim(), out var ms) && ms > 0 ? ms : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static string[]? ReadClock(string clockPath, string queueId)
    {
        if (string.IsNullOrWhiteSpace(queueId) || !File.Exists(clockPath))
            return null;
        var lines = File.ReadAllLines(clockPath);
        if (lines.Length == 0 || lines[0].Trim() != queueId)
            return null;
        return lines;
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
