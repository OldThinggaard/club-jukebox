using System.Diagnostics;

namespace Jukebox.Core.Playback;

public sealed record VolumeReport(int Percent, bool Socket, bool Pipe, bool Alsa);

/// <summary>
/// Turns the idle jack off and sets the one output that is already playing.
/// Both jacks at once cancel the bass.
/// </summary>
public static class LiveVolume
{
    public const string SocketPath = "/tmp/jukebox-mpv.sock";

    public static VolumeReport Apply(string dataDirectory, int percent, string? socketPath = null)
    {
        percent = Math.Clamp(percent, 0, 100);
        var report = new VolumeReport(percent, false, false, TryActive(percent));
        try
        {
            HallPlayback.AtomicWrite(Path.Combine(dataDirectory, "volume.heard"), HeardText(report));
        }
        catch (IOException)
        {
            // The buttons already saved the number.
        }

        return report;
    }

    public static VolumeReport? Read(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, "volume.heard");
            if (!File.Exists(path))
                return null;
            int? percent = null;
            var socket = false;
            var pipe = false;
            var alsa = false;
            foreach (var line in File.ReadAllLines(path))
            {
                var cut = line.IndexOf('=');
                if (cut <= 0)
                    continue;
                var key = line[..cut].Trim();
                var value = line[(cut + 1)..].Trim();
                if (key == "PERCENT" && int.TryParse(value, out var parsed))
                    percent = parsed;
                else if (key == "SOCKET")
                    socket = value == "1";
                else if (key == "PIPE")
                    pipe = value == "1";
                else if (key == "ALSA")
                    alsa = value == "1";
            }

            return percent is int heard ? new VolumeReport(heard, socket, pipe, alsa) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static string HeardText(VolumeReport report) =>
        $"PERCENT={report.Percent}\nSOCKET={(report.Socket ? "1" : "0")}\nPIPE={(report.Pipe ? "1" : "0")}\nALSA={(report.Alsa ? "1" : "0")}\n";

    private static bool TryActive(int percent)
    {
        var script = Environment.GetEnvironmentVariable("JUKEBOX_VOLUME_SCRIPT");
        if (string.IsNullOrWhiteSpace(script))
            script = "/opt/jukebox/lib/mpv-volume.py";
        if (!File.Exists(script))
            return false;
        try
        {
            var start = new ProcessStartInfo("python3")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add(script);
            start.ArgumentList.Add("active");
            start.ArgumentList.Add(percent.ToString());
            using var process = Process.Start(start);
            if (process is null)
                return false;
            if (!process.WaitForExitAsync().Wait(2000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
