using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jukebox.Core.Playback;

public sealed record VolumeReport(int Percent, bool Socket, bool Pipe, bool Alsa);

/// <summary>
/// Sends the on-screen percent to the sound that is already playing.
/// The number used to be stored, while the running file kept the level it
/// started with. That start level is 80.
/// </summary>
public static partial class LiveVolume
{
    public const string SocketPath = "/tmp/jukebox-mpv.sock";

    public static VolumeReport Apply(string dataDirectory, int percent, string? socketPath = null)
    {
        percent = Math.Clamp(percent, 0, 100);
        var report = new VolumeReport(percent, TryMpv(socketPath ?? SocketPath, percent), TryPipe(percent), TryAlsa(percent));
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

    private static bool TryMpv(string socketPath, int percent)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var connect = socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
            if (!connect.Wait(TimeSpan.FromMilliseconds(400)))
                return false;
            socket.SendTimeout = 400;
            socket.ReceiveTimeout = 400;
            if (!Send(socket, "volume", percent))
                return false;
            Send(socket, "mute", percent == 0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool Send(Socket socket, string property, object value)
    {
        var json = JsonSerializer.Serialize(new { command = new object[] { "set_property", property, value } }) + "\n";
        socket.Send(Encoding.UTF8.GetBytes(json));
        var got = new StringBuilder();
        var buffer = new byte[512];
        while (got.ToString().IndexOf('\n') < 0)
        {
            var read = socket.Receive(buffer);
            if (read <= 0)
                return false;
            got.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }

        return got.ToString().Contains("success", StringComparison.Ordinal);
    }

    private static bool TryPipe(int percent)
    {
        var level = (percent / 100d).ToString("0.00", CultureInfo.InvariantCulture);
        var mute = percent == 0 ? "1" : "0";
        var any = Run("wpctl", $"set-mute @DEFAULT_AUDIO_SINK@ {mute}", 800);
        any = Run("wpctl", $"set-volume @DEFAULT_AUDIO_SINK@ {level}", 800) || any;
        var status = Capture("wpctl", "status", 800);
        if (string.IsNullOrWhiteSpace(status))
            return any;
        var mode = "";
        var count = 0;
        foreach (var line in status.Split('\n'))
        {
            if (line.Contains("Sinks:", StringComparison.Ordinal))
            {
                mode = "sink";
                continue;
            }

            if (line.Contains("Sources:", StringComparison.Ordinal) && mode == "sink")
            {
                mode = "";
                continue;
            }

            if (line.Contains("Streams:", StringComparison.Ordinal))
            {
                mode = "stream";
                continue;
            }

            if (mode == "stream" && (line.Contains("Filters:", StringComparison.Ordinal) || line.Contains("Video", StringComparison.Ordinal)))
            {
                mode = "";
                continue;
            }

            if (mode is not ("sink" or "stream") || count >= 4)
                continue;
            var match = NodePattern().Match(line);
            if (!match.Success)
                continue;
            count++;
            var id = match.Groups[1].Value;
            any = Run("wpctl", $"set-mute {id} {mute}", 400) || any;
            any = Run("wpctl", $"set-volume {id} {level}", 400) || any;
        }

        return any;
    }

    private static bool TryAlsa(int percent)
    {
        var tail = percent == 0 ? "mute" : "unmute";
        var any = false;
        foreach (var card in Cards())
        {
            foreach (var name in new[] { "Master", "Headphone", "Speaker", "PCM" })
                any = Run("amixer", $"-c {card} -q sset {name} {percent}% {tail}", 400) || any;
        }

        any = Run("amixer", $"-q sset Master {percent}% {tail}", 400) || any;
        any = Run("amixer", $"-q sset Headphone {percent}% {tail}", 400) || any;
        return any;
    }

    private static IEnumerable<string> Cards()
    {
        var found = new List<string>();
        try
        {
            if (!File.Exists("/proc/asound/cards"))
                return found;
            foreach (var line in File.ReadAllLines("/proc/asound/cards"))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && parts[0].All(char.IsDigit))
                    found.Add(parts[0]);
                if (found.Count >= 3)
                    break;
            }
        }
        catch (IOException)
        {
            return found;
        }

        return found;
    }

    private static string? Capture(string file, string args, int timeoutMs)
    {
        try
        {
            var start = new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var process = Process.Start(start);
            if (process is null)
                return null;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExitAsync().Wait(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return null;
            }

            if (!stdout.Wait(200))
                return null;
            _ = stderr.Wait(100);
            return process.ExitCode == 0 ? stdout.Result : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool Run(string file, string args, int timeoutMs) => Capture(file, args, timeoutMs) is not null;

    [GeneratedRegex(@"(?:^|\s)(\d+)\.")]
    private static partial Regex NodePattern();
}
