using System.Diagnostics;
using Jukebox.Core.Network;

namespace Jukebox.Host;

internal static class WirelessProbe
{
    private static readonly object Gate = new();
    private static DateTime _freshUntil = DateTime.MinValue;
    private static WirelessLink? _current;

    public static WirelessLink? Current()
    {
        lock (Gate)
        {
            if (DateTime.UtcNow < _freshUntil)
                return _current;
            _current = WirelessLink.Parse(Read());
            _freshUntil = DateTime.UtcNow.AddSeconds(20);
            return _current;
        }
    }

    private static string? Read()
    {
        try
        {
            var start = new ProcessStartInfo("nmcli")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = false
            };
            start.ArgumentList.Add("-t");
            start.ArgumentList.Add("-f");
            start.ArgumentList.Add("IN-USE,SSID,SIGNAL");
            start.ArgumentList.Add("device");
            start.ArgumentList.Add("wifi");
            start.ArgumentList.Add("list");
            start.ArgumentList.Add("--rescan");
            start.ArgumentList.Add("no");
            using var process = Process.Start(start);
            if (process is null)
                return null;
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(2000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return null;
            }

            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
