using System.Diagnostics;
using System.Net;
using Jukebox.Core.Install;
using Jukebox.Core.Qr;

namespace Jukebox.Host;

public static class InstallEndpoints
{
    private const string OnlimeUrl = "https://cloud.onlime.dk/web/secure";
    private static readonly string StateDir = "/tmp/jukebox-install";
    private static int _installing;

    public static void Map(WebApplication app, JukeboxConfig config)
    {
        var group = app.MapGroup("/api/install");
        group.MapGet("/state", () => Results.Ok(new
        {
            installerMode = config.InstallerMode,
            onlimeUrl = OnlimeUrl
        }));
        group.MapGet("/onlime-qr.svg", () => Results.Text(QrImages.Svg(OnlimeUrl), "image/svg+xml"));
        group.MapPost("/open-onlime", (HttpContext http) =>
        {
            if (!IsLocal(http))
                return Results.Json(new { opened = false, reason = "Kun touchskærmen kan åbne Onlime." }, statusCode: StatusCodes.Status403Forbidden);
            return OpenOnlimeBrowser();
        });
        group.MapGet("/disks", () =>
        {
            var json = Run("lsblk", "-J", "-o", "NAME,SIZE,TYPE,MODEL,TRAN,RM,LABEL,MOUNTPOINT");
            var disks = string.IsNullOrWhiteSpace(json) ? [] : InstallDisks.Parse(json);
            return Results.Ok(disks.Select(disk => new
            {
                name = disk.Name,
                path = disk.Path,
                size = disk.Size,
                model = string.IsNullOrWhiteSpace(disk.Model) ? "Harddisk" : disk.Model.Trim(),
                transport = disk.Transport,
                removable = disk.Removable
            }));
        });
        group.MapGet("/wifi", ListWifi);
        group.MapGet("/status", () =>
        {
            var path = Path.Combine(StateDir, "status");
            var lines = File.Exists(path) ? File.ReadAllLines(path) : [];
            var error = lines.LastOrDefault(line => line.StartsWith("FEJL:", StringComparison.Ordinal));
            var done = lines.Any(line => line.Contains("genstarter", StringComparison.OrdinalIgnoreCase) || line.Contains("færdig", StringComparison.OrdinalIgnoreCase));
            return Results.Ok(new { lines, error, done });
        });
        group.MapPost("/wifi", async (WifiBody body, HttpContext http) =>
        {
            if (!config.InstallerMode)
                return Results.Json(new { ok = false, reason = "Trådløst netværk sættes op under Administration efter installationen." }, statusCode: StatusCodes.Status403Forbidden);
            if (!IsLocal(http))
                return Results.Json(new { ok = false, reason = "Kun touchskærmen kan ændre netværket." }, statusCode: StatusCodes.Status403Forbidden);
            return await ConnectWifi(body.Ssid, body.Psk);
        });
        group.MapPost("/onlime", async (OnlimeBody body, HttpContext http) =>
        {
            if (!config.InstallerMode)
                return Results.Json(new { ok = false, reason = "Onlime sættes op under Administration efter installationen." }, statusCode: StatusCodes.Status403Forbidden);
            if (!IsLocal(http))
                return Results.Json(new { ok = false, reason = "Kun touchskærmen kan logge på Onlime." }, statusCode: StatusCodes.Status403Forbidden);
            return await Begin(body.Token, body.Device, startSync: false);
        });
        group.MapPost("/run", (InstallBody body, HttpContext http) =>
        {
            if (!config.InstallerMode)
                return Results.Json(new { ok = false, reason = "Harddisken kan kun slettes fra installations-USB'en." }, statusCode: StatusCodes.Status403Forbidden);
            if (!IsLocal(http))
                return Results.Json(new { ok = false, reason = "Kun touchskærmen kan starte installationen." }, statusCode: StatusCodes.Status403Forbidden);
            var path = body.Path ?? "";
            if (!InstallDisks.IsInstallTarget(path))
                return Results.BadRequest(new { ok = false, reason = "Vælg computerens harddisk." });
            var name = Path.GetFileName(path);
            if (!string.Equals(body.Confirm, name, StringComparison.Ordinal))
                return Results.BadRequest(new { ok = false, reason = "Bekræft den valgte disk." });
            if (Interlocked.Exchange(ref _installing, 1) == 1)
                return Results.Json(new { ok = false, reason = "Installationen kører allerede." }, statusCode: StatusCodes.Status409Conflict);
            var script = "/opt/jukebox/lib/install-to-disk.sh";
            if (!File.Exists(script))
            {
                Interlocked.Exchange(ref _installing, 0);
                return Results.Json(new { ok = false, reason = "Installationsprogrammet findes kun på USB-nøglen." }, statusCode: StatusCodes.Status400BadRequest);
            }

            Directory.CreateDirectory(StateDir);
            var statusPath = Path.Combine(StateDir, "status");
            File.WriteAllText(statusPath, "Starter installationen.\n");
            var start = Command.Start("sudo", "-n", script, "--target", path, "--confirm", name, "--state", StateDir, "--reboot");
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            var output = new System.Text.StringBuilder();
            process.OutputDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) output.AppendLine(eventArgs.Data); };
            process.ErrorDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) output.AppendLine(eventArgs.Data); };
            process.Exited += (_, _) => Interlocked.Exchange(ref _installing, 0);
            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("sudo blev ikke startet.");
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _installing, 0);
                var reason = "Installationen kunne ikke starte. " + ex.Message;
                File.AppendAllText(statusPath, "FEJL: " + reason + "\n");
                return Results.Json(new { ok = false, reason }, statusCode: StatusCodes.Status500InternalServerError);
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            // sudo exits at once when it is not allowed to become root. The disk script keeps running.
            if (process.WaitForExit(2500))
            {
                process.WaitForExit();
                Interlocked.Exchange(ref _installing, 0);
                if (process.ExitCode != 0)
                {
                    var detail = output.ToString().Trim();
                    if (detail.Contains("no new privileges", StringComparison.OrdinalIgnoreCase))
                        detail = "Programmet må ikke skrive til harddisken.";
                    var line = detail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault(item => item.StartsWith("FEJL:", StringComparison.Ordinal));
                    var reason = line ?? (string.IsNullOrWhiteSpace(detail) ? "Installationen kunne ikke starte." : detail);
                    File.AppendAllText(statusPath, reason.StartsWith("FEJL:", StringComparison.Ordinal) ? reason + "\n" : "FEJL: " + reason + "\n");
                    return Results.Json(new { ok = false, reason }, statusCode: StatusCodes.Status500InternalServerError);
                }
            }

            return Results.Ok(new { ok = true });
        });
    }

    public static IResult ListWifi()
    {
        var text = Run("nmcli", "-t", "-f", "SSID,SIGNAL,SECURITY", "device", "wifi", "list", "--rescan", "yes");
        return Results.Ok(ParseWifi(text));
    }

    public static async Task<IResult> ConnectWifi(string? rawSsid, string? rawPsk)
    {
        var ssid = (rawSsid ?? "").Trim();
        var psk = rawPsk ?? "";
        if (ssid.Length is 0 or > 64 || ssid.Contains('\n') || ssid.Contains('\r') || psk.Contains('\n') || psk.Contains('\r'))
            return Results.BadRequest(new { ok = false, reason = "Netværksnavnet kan ikke bruges." });
        Directory.CreateDirectory(StateDir);
        await File.WriteAllTextAsync(Path.Combine(StateDir, "ssid"), ssid);
        await File.WriteAllTextAsync(Path.Combine(StateDir, "psk"), psk);
        var script = "/opt/jukebox/lib/wifi-connect.sh";
        if (!File.Exists(script))
            return Results.Json(new { ok = false, reason = "Wi-Fi sættes på den installerede maskine." }, statusCode: StatusCodes.Status400BadRequest);
        var (code, output) = RunWait("sudo", "-n", script, StateDir);
        return code == 0
            ? Results.Ok(new { ok = true, message = $"Forbundet til {ssid}" })
            : Results.BadRequest(new { ok = false, reason = string.IsNullOrWhiteSpace(output) ? "Kunne ikke forbinde til netværket." : output.Trim() });
    }

    public static IResult OpenOnlimeBrowser()
    {
        var script = "/opt/jukebox/lib/onlime-open.sh";
        if (!File.Exists(script))
            return Results.Ok(new { opened = false, url = OnlimeUrl });
        var start = new ProcessStartInfo(script) { UseShellExecute = false };
        if (Process.Start(start) is null)
            return Results.Ok(new { opened = false, url = OnlimeUrl });
        return Results.Ok(new { opened = true, url = OnlimeUrl });
    }

    public static async Task<IResult> Begin(string? rawToken, string? rawDevice, bool startSync)
    {
        var token = new string((rawToken ?? "").Where(ch => !char.IsWhiteSpace(ch)).ToArray());
        var device = string.IsNullOrWhiteSpace(rawDevice) ? "videojukebox" : rawDevice.Trim();
        if (token.Length < 8 || device.Any(ch => char.IsWhiteSpace(ch)))
            return Results.BadRequest(new { ok = false, reason = "Login-koden fra Onlime ser ikke rigtig ud." });
        Directory.CreateDirectory(StateDir);
        var tokenPath = Path.Combine(StateDir, "onlime.token");
        await File.WriteAllTextAsync(tokenPath, token);
        try { File.SetUnixFileMode(tokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* Windows tests never reach this. */ }
        var script = "/opt/jukebox/lib/onlime-login.sh";
        if (!File.Exists(script))
        {
            File.Delete(tokenPath);
            return Results.Json(new { ok = false, reason = "Onlime-programmet findes kun på den installerede maskine." }, statusCode: StatusCodes.Status400BadRequest);
        }

        var (code, output) = startSync
            ? RunWait("sudo", "-n", script, tokenPath, device, "/var/lib/jukebox/onlime-sync", "--start")
            : RunWait("sudo", "-n", script, tokenPath, device);
        File.Delete(tokenPath);
        var safe = output.Replace(token, "••••", StringComparison.Ordinal);
        if (code != 0)
            return Results.BadRequest(new { ok = false, reason = string.IsNullOrWhiteSpace(safe) ? "Onlime afviste login-koden. Lav en ny kode og prøv igen." : safe.Trim() });
        return Results.Ok(new
        {
            ok = true,
            message = startSync
                ? "Onlime er logget ind. Sync henter filmene ned nu."
                : "Onlime er logget ind. Sync-mappen hentes ned efter installationen."
        });
    }

    private static bool IsLocal(HttpContext http)
    {
        var ip = http.Connection.RemoteIpAddress;
        if (ip is null)
            return true;
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        return IPAddress.IsLoopback(ip);
    }

    private static IReadOnlyList<object> ParseWifi(string? text)
    {
        var list = new List<object>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split(':');
            if (parts.Length < 2)
                continue;
            var ssid = parts[0].Replace("\\:", ":", StringComparison.Ordinal).Trim();
            if (ssid.Length == 0 || !seen.Add(ssid))
                continue;
            _ = int.TryParse(parts[1], out var signal);
            var security = parts.Length > 2 ? parts[2] : "";
            list.Add(new { ssid, signal, secure = security.Length > 0 && !security.Equals("--", StringComparison.Ordinal) });
        }

        return list;
    }

    private static string? Run(string file, params string[] args)
    {
        try
        {
            var (code, output) = RunWait(file, args);
            return code == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private static (int Code, string Output) RunWait(string file, params string[] args)
    {
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException(file);
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(150_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return (1, "Det tog for lang tid.");
        }

        var output = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        return (process.ExitCode, output);
    }
}

public sealed record WifiBody(string? Ssid, string? Psk);
public sealed record OnlimeBody(string? Token, string? Device);
public sealed record InstallBody(string? Path, string? Confirm);
