using System.Globalization;

namespace Jukebox.Core.Playback;

public sealed record ScreenRestartDecision(bool HoldHouse, bool Restart);

/// <summary>
/// Once a day, after the chosen clock time, both screens close and open again.
/// The machine has to have been on at that time. A song, a guest queue, or a
/// finger on the shelf holds it back.
/// </summary>
public static class ScreenRestart
{
    public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(2);

    public static ScreenRestartDecision Decide(
        DateTime localNow,
        int hour,
        int minute,
        DateTime sessionStartedLocal,
        string? restartedDay,
        bool songPlaying,
        bool guestWaiting,
        DateTime lastTouchUtc,
        DateTime utcNow)
    {
        hour = Math.Clamp(hour, 0, 23);
        minute = Math.Clamp(minute, 0, 59);
        var slot = localNow.Date.AddHours(hour).AddMinutes(minute);
        var today = localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var waiting = localNow >= slot
            && sessionStartedLocal < slot
            && restartedDay != today;
        if (!waiting)
            return new ScreenRestartDecision(false, false);

        var quiet = utcNow - lastTouchUtc >= Quiet;
        return new ScreenRestartDecision(true, !songPlaying && !guestWaiting && quiet);
    }

    public static DateTime Local(DateTime utc, string timeZoneId)
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            zone = TimeZoneInfo.Utc;
        }

        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
    }
}
