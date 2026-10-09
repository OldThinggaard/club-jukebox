using System.Diagnostics;

namespace Jukebox.Host;

public static class OnlimeSync
{
    public const string ScriptPath = "/opt/jukebox/lib/onlime-install.sh";
    public const string ClientPath = "/usr/bin/jotta-cli";

    public static object Status()
    {
        if (File.Exists(ClientPath))
        {
            return new
            {
                installed = true,
                canInstall = true,
                message = "Onlime Sync er installeret. Log ind, og kopiér koden."
            };
        }

        if (!File.Exists(ScriptPath))
        {
            return new
            {
                installed = false,
                canInstall = false,
                message = "På klubmaskinen henter denne knap Onlime Sync. Her er det kun en forhåndsvisning."
            };
        }

        return new
        {
            installed = false,
            canInstall = true,
            message = "Onlime Sync er ikke installeret. Sæt kablet i, og tryk Hent Onlime Sync."
        };
    }

    public static (bool Ok, int Status, string Message) Install()
    {
        if (File.Exists(ClientPath))
            return (true, StatusCodes.Status200OK, "Onlime Sync er installeret.");
        if (!File.Exists(ScriptPath))
            return (false, StatusCodes.Status403Forbidden, "Onlime Sync kan kun hentes på klubmaskinen.");

        var (code, output) = Run(TimeSpan.FromMinutes(8));
        if (code != 0 || !File.Exists(ClientPath))
        {
            return (false, StatusCodes.Status500InternalServerError, string.IsNullOrWhiteSpace(output)
                ? "Onlime Sync kunne ikke hentes. Sæt netværkskablet i, og prøv igen."
                : output);
        }

        return (true, StatusCodes.Status200OK, "Onlime Sync er installeret. Log ind, og kopiér koden.");
    }

    private static (int Code, string Output) Run(TimeSpan timeout)
    {
        var start = new ProcessStartInfo("sudo", "-n")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(ScriptPath);
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
                return (1, "Hentningen tog for lang tid. Sæt kablet i, og prøv igen.");
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            return (process.ExitCode, PickMessage(stdout, stderr));
        }
        catch (Exception ex)
        {
            return (1, ex.Message);
        }
    }

    private static string PickMessage(string stdout, string stderr)
    {
        var lines = (stdout + "\n" + stderr).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var danish = lines.LastOrDefault(line => line.StartsWith("Onlime", StringComparison.Ordinal));
        var line = string.IsNullOrWhiteSpace(danish) ? lines.LastOrDefault() ?? "" : danish;
        return line.Length > 180 ? line[..180] : line;
    }
}
