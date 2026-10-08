using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Jukebox.Core.Updates;

public sealed record UpdatePackageInfo(string Path, string Version, bool Valid, string? Error, bool Newer);

public static class VersionCompare
{
    public static int Compare(string left, string right)
    {
        var a = Parts(left);
        var b = Parts(right);
        var length = Math.Max(a.Length, b.Length);
        for (var i = 0; i < length; i++)
        {
            var av = i < a.Length ? a[i] : 0;
            var bv = i < b.Length ? b[i] : 0;
            if (av != bv)
                return av.CompareTo(bv);
        }

        return 0;
    }

    private static int[] Parts(string value) =>
        value.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(new string(part.TakeWhile(char.IsDigit).ToArray()), out var number) ? number : 0)
            .ToArray();
}

public static class UpdateCatalog
{
    public static IReadOnlyList<UpdatePackageInfo> Scan(IEnumerable<string> roots, string currentVersion)
    {
        var list = new List<UpdatePackageInfo>();
        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.Ordinal))
        {
            foreach (var package in Directory.EnumerateFiles(root, "*.tar.gz", SearchOption.AllDirectories).Take(40))
            {
                list.Add(Inspect(package, currentVersion));
            }
        }

        return list.OrderByDescending(item => item.Version, Comparer<string>.Create(VersionCompare.Compare)).ToList();
    }

    public static UpdatePackageInfo Inspect(string packagePath, string currentVersion)
    {
        var temp = Directory.CreateTempSubdirectory("jukebox-pkg-");
        try
        {
            var extracted = Run("tar", $"-xzf \"{packagePath}\" -C \"{temp.FullName}\" manifest.json SHA256SUMS");
            if (!extracted)
                return new UpdatePackageInfo(packagePath, "", false, "The package could not be read.", false);
            var manifestPath = Path.Combine(temp.FullName, "manifest.json");
            var sumsPath = Path.Combine(temp.FullName, "SHA256SUMS");
            if (!File.Exists(manifestPath) || !File.Exists(sumsPath))
                return new UpdatePackageInfo(packagePath, "", false, "manifest.json and SHA256SUMS are required.", false);
            var version = ReadVersion(manifestPath);
            if (string.IsNullOrWhiteSpace(version))
                return new UpdatePackageInfo(packagePath, "", false, "The manifest has no version.", false);
            var newer = VersionCompare.Compare(version, currentVersion) > 0;
            return new UpdatePackageInfo(packagePath, version, true, null, newer);
        }
        catch (Exception ex)
        {
            return new UpdatePackageInfo(packagePath, "", false, ex.Message, false);
        }
        finally
        {
            temp.Delete(true);
        }
    }

    public static bool VerifyExtracted(string directory, out string error)
    {
        var sumsPath = Path.Combine(directory, "SHA256SUMS");
        if (!File.Exists(sumsPath))
        {
            error = "SHA256SUMS is missing.";
            return false;
        }

        foreach (var line in File.ReadAllLines(sumsPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                error = "A checksum line is malformed.";
                return false;
            }

            var expected = parts[0];
            var relative = parts[^1].TrimStart('*');
            var full = Path.GetFullPath(Path.Combine(directory, relative));
            if (!full.StartsWith(directory, StringComparison.Ordinal))
            {
                error = "A checksum path escapes the package.";
                return false;
            }

            if (!File.Exists(full))
            {
                error = $"Missing {relative}.";
                return false;
            }

            var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full))).ToLowerInvariant();
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                error = $"Checksum mismatch for {relative}.";
                return false;
            }
        }

        error = "";
        return true;
    }

    public static string? ReadVersion(string manifestPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        return document.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;
    }

    private static bool Run(string file, string args)
    {
        var start = new ProcessStartInfo("/bin/bash", $"-lc \"{file} {args}\"")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        using var process = Process.Start(start);
        if (process is null)
            return false;
        process.WaitForExit(15000);
        return process.ExitCode == 0;
    }
}

public sealed class UpdateApplicator
{
    public static int ApplyIfAvailable(string appDirectory, string dataDirectory, IEnumerable<string> roots)
    {
        var pidFile = Path.Combine(dataDirectory, "host.pid");
        if (IsProcessAlive(pidFile))
        {
            Console.WriteLine("Jukebox is running. Updates wait until the next boot.");
            return 0;
        }

        var packages = UpdateCatalog.Scan(roots, JukeboxVersion.Current);
        var candidate = packages.FirstOrDefault(item => item.Valid && item.Newer);
        if (candidate is null)
        {
            Console.WriteLine("No newer update package.");
            return 0;
        }

        var parent = Directory.GetParent(appDirectory)?.FullName ?? appDirectory;
        var staging = Path.Combine(parent, "staging");
        var previous = Path.Combine(parent, "previous");
        if (Directory.Exists(staging))
            Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        if (!RunTar(candidate.Path, staging))
        {
            Console.WriteLine("Could not extract the update.");
            return 0;
        }

        var payload = Directory.Exists(Path.Combine(staging, "app")) ? Path.Combine(staging, "app") : staging;
        if (!UpdateCatalog.VerifyExtracted(staging, out var error))
        {
            Console.WriteLine(error);
            Directory.Delete(staging, true);
            return 0;
        }

        if (!SelfCheck(payload, dataDirectory))
        {
            Console.WriteLine("The new version failed its self-check. The current version stays in place.");
            Directory.Delete(staging, true);
            return 0;
        }

        if (Directory.Exists(previous))
            Directory.Delete(previous, true);
        if (Directory.Exists(appDirectory))
            Directory.Move(appDirectory, previous);
        Directory.Move(payload, appDirectory);
        if (!SelfCheck(appDirectory, dataDirectory))
        {
            Console.WriteLine("Verification failed after install. Rolling back.");
            if (Directory.Exists(appDirectory))
                Directory.Delete(appDirectory, true);
            if (Directory.Exists(previous))
                Directory.Move(previous, appDirectory);
            return Directory.Exists(appDirectory) ? 0 : 1;
        }

        var updaterSource = Path.Combine(staging, "updater");
        var updaterTarget = Path.Combine(parent, "updater");
        if (Directory.Exists(updaterSource))
            CopyTree(updaterSource, updaterTarget);
        if (Directory.Exists(staging))
            Directory.Delete(staging, true);
        Console.WriteLine($"Installed {candidate.Version}.");
        return 0;
    }

    private static bool SelfCheck(string directory, string dataDirectory)
    {
        var exe = Path.Combine(directory, "Jukebox.Host");
        var dll = Path.Combine(directory, "Jukebox.Host.dll");
        ProcessStartInfo start;
        if (File.Exists(exe))
            start = new ProcessStartInfo(exe, "--self-check");
        else if (File.Exists(dll))
            start = new ProcessStartInfo("dotnet", $"\"{dll}\" --self-check");
        else
            return false;
        start.UseShellExecute = false;
        start.Environment["JUKEBOX_DATA_DIR"] = dataDirectory;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = Process.Start(start);
        if (process is null)
            return false;
        process.WaitForExit(20000);
        return process.ExitCode == 0;
    }

    private static bool RunTar(string package, string destination)
    {
        var start = new ProcessStartInfo("tar", $"-xzf \"{package}\" -C \"{destination}\"")
        {
            UseShellExecute = false,
            RedirectStandardError = true
        };
        using var process = Process.Start(start);
        process?.WaitForExit(60000);
        return process is { ExitCode: 0 };
    }

    private static bool IsProcessAlive(string pidFile)
    {
        if (!File.Exists(pidFile) || !int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid))
            return false;
        try
        {
            var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }
}
