using System.Diagnostics;

namespace Jukebox.Host;

public static class UsbPackage
{
    public const string ScriptPath = "/opt/jukebox/lib/usb-package.py";

    public static object Status()
    {
        if (!File.Exists(ScriptPath))
        {
            return new
            {
                found = false,
                canInstall = false,
                busy = false,
                sizeBytes = 0L,
                message = "På klubmaskinen installerer denne knap pakken fra USB-nøglen. Her er det kun en forhåndsvisning."
            };
        }

        if (InstallUnitActive())
        {
            return new
            {
                found = true,
                canInstall = true,
                busy = true,
                sizeBytes = 0L,
                message = "Installerer. Skærmen slukker, og maskinen starter igen. Lad den være."
            };
        }

        var (code, output) = Run(ScriptPath, "find", TimeSpan.FromSeconds(30));
        var path = output.Trim();
        if (code != 0 || string.IsNullOrWhiteSpace(path) || !path.EndsWith("videojukebox.deb", StringComparison.Ordinal))
        {
            return new
            {
                found = false,
                canInstall = true,
                busy = false,
                sizeBytes = 0L,
                message = "Sæt USB-nøglen med videojukebox.deb i maskinen."
            };
        }

        return new
        {
            found = true,
            canInstall = true,
            busy = false,
            sizeBytes = Size(path),
            message = "USB-nøglen er klar. Tryk Installer og genstart."
        };
    }

    public static (bool Ok, int Status, string Message) StartInstall()
    {
        if (!File.Exists(ScriptPath))
            return (false, StatusCodes.Status403Forbidden, "Pakken kan kun installeres på klubmaskinen.");
        if (InstallUnitActive())
            return (true, StatusCodes.Status200OK, "Installerer. Skærmen slukker, og maskinen starter igen. Lad den være.");

        Run("systemctl", "reset-failed", TimeSpan.FromSeconds(5), "jukebox-usb-install");
        var (code, output) = Run("systemd-run", "--collect", TimeSpan.FromSeconds(15), "--unit=jukebox-usb-install", ScriptPath, "install");
        if (code != 0)
            return (false, StatusCodes.Status500InternalServerError, "Installationen kunne ikke starte. " + FirstLine(output));
        return (true, StatusCodes.Status200OK, "Installerer. Skærmen slukker, og maskinen starter igen. Lad den være.");
    }

    private static long Size(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static bool InstallUnitActive()
    {
        var (code, output) = Run("systemctl", "is-active", TimeSpan.FromSeconds(5), "jukebox-usb-install");
        return code == 0 && output.Trim() is "active" or "activating";
    }

    private static (int Code, string Output) Run(string file, string arg1, TimeSpan timeout, params string[] rest)
    {
        var start = new ProcessStartInfo("sudo", "-n")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (file != "sudo")
            start.ArgumentList.Add(file);
        start.ArgumentList.Add(arg1);
        foreach (var arg in rest)
            start.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return (1, "");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return (1, "");
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            var text = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            return (process.ExitCode, text);
        }
        catch (Exception ex)
        {
            return (1, ex.Message);
        }
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        return line.Length > 180 ? line[..180] : line;
    }
}
