using System.Globalization;

namespace Jukebox.Core.Playback;

public static class PowerGap
{
    public static readonly TimeSpan ForgetQueueAfter = TimeSpan.FromMinutes(30);

    public static bool ForgetQueue(DateTime? lastSeenUtc, DateTime nowUtc)
    {
        if (lastSeenUtc is null)
            return true;
        return nowUtc - lastSeenUtc.Value > ForgetQueueAfter;
    }

    public static DateTime? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var text = File.ReadAllText(path).Trim();
            if (text.Length == 0)
                return null;
            return ClubCulture.ParseIso(text).ToUniversalTime();
        }
        catch (Exception ex) when (ex is IOException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    public static void Touch(string path) => Write(path, DateTime.UtcNow);

    public static void Write(string path, DateTime utc)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);
        var temp = path + ".tmp";
        File.WriteAllText(temp, ClubCulture.Iso(utc));
        File.Move(temp, path, true);
    }
}
