using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Jukebox.Core.Playback;

public sealed record VolumeReport(int Percent, bool Socket, bool Pipe, bool Alsa);

/// <summary>
/// Sends the on-screen percent to the one output that is already playing.
/// The other output is left untouched, so the two cannot cancel the bass.
/// </summary>
public static class LiveVolume
{
    public const string SocketPath = "/tmp/jukebox-mpv.sock";

    public static VolumeReport Apply(string dataDirectory, int percent, string? socketPath = null)
    {
        percent = Math.Clamp(percent, 0, 100);
        var report = new VolumeReport(percent, TryMpv(socketPath ?? SocketPath, percent), false, false);
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
            return Send(socket, "ao-volume", percent);
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
}
