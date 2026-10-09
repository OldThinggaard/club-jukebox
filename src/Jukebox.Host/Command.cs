using System.Diagnostics;

namespace Jukebox.Host;

public static class Command
{
    public static ProcessStartInfo Start(string file, params string[] args)
    {
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        return start;
    }
}
