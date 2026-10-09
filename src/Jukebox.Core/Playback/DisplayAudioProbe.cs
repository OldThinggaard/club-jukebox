using System.Text.RegularExpressions;

namespace Jukebox.Core.Playback;

public sealed record DisplayInfo(string Name, int Width, int Height, int X, int Y, bool Connected, bool Primary);

public sealed record AudioDevice(string Id, string Label, string Kind);

public static partial class DisplayAudioProbe
{
    public static IReadOnlyList<DisplayInfo> ParseXrandr(string text)
    {
        var list = new List<DisplayInfo>();
        foreach (Match match in OutputPattern().Matches(text))
        {
            var connected = match.Groups["state"].Value == "connected";
            if (!connected)
            {
                list.Add(new DisplayInfo(match.Groups["name"].Value, 0, 0, 0, 0, false, false));
                continue;
            }

            var hasMode = match.Groups["w"].Success;
            list.Add(new DisplayInfo(
                match.Groups["name"].Value,
                hasMode ? int.Parse(match.Groups["w"].Value) : 0,
                hasMode ? int.Parse(match.Groups["h"].Value) : 0,
                hasMode ? int.Parse(match.Groups["x"].Value) : 0,
                hasMode ? int.Parse(match.Groups["y"].Value) : 0,
                true,
                match.Groups["primary"].Success));
        }

        return list;
    }

    public static (string? Ui, string? Playback) AutoAssign(IReadOnlyList<DisplayInfo> displays)
    {
        var connected = displays.Where(item => item.Connected && item.Width > 0).ToList();
        if (connected.Count == 0)
            return (null, null);

        var ui = connected.FirstOrDefault(item => IsInternal(item.Name)) ?? connected[0];
        var playback = connected.FirstOrDefault(item => item.Name != ui.Name && IsExternal(item.Name))
                       ?? connected.FirstOrDefault(item => item.Name != ui.Name);
        return (ui.Name, playback?.Name);
    }

    public static int ScreenIndex(IReadOnlyList<DisplayInfo> displays, string? outputName)
    {
        var connected = displays.Where(item => item.Connected && item.Width > 0).ToList();
        if (connected.Count == 0)
            return 0;
        if (!string.IsNullOrWhiteSpace(outputName))
        {
            var index = connected.FindIndex(item => item.Name.Equals(outputName, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                return index;
        }

        var external = connected.FindIndex(item => IsExternal(item.Name));
        return external >= 0 ? external : connected.Count - 1;
    }

    public static bool IsInternal(string name) =>
        name.StartsWith("eDP", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("LVDS", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("DSI", StringComparison.OrdinalIgnoreCase)
        || name.Contains("touch", StringComparison.OrdinalIgnoreCase);

    public static bool IsExternal(string name) =>
        name.StartsWith("HDMI", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("DP", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("VGA", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("DisplayPort", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<DisplayInfo> QueryDisplays()
    {
        var output = Run("xrandr", "--query");
        return string.IsNullOrWhiteSpace(output) ? [] : ParseXrandr(output);
    }

    public static IReadOnlyList<AudioDevice> ParseSinks(string text)
    {
        var devices = new List<AudioDevice>();
        string? sink = null;
        var inPorts = false;
        var portsIndent = -1;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;
            var indent = line.Length - line.TrimStart().Length;

            if (trimmed.StartsWith("Name:", StringComparison.Ordinal))
            {
                sink = trimmed["Name:".Length..].Trim();
                inPorts = false;
                continue;
            }

            if (sink is null)
                continue;
            if (trimmed.Equals("Ports:", StringComparison.Ordinal))
            {
                inPorts = true;
                portsIndent = indent;
                continue;
            }

            if (!inPorts || indent <= portsIndent)
            {
                inPorts = false;
                continue;
            }

            var colon = trimmed.IndexOf(':');
            if (colon <= 0)
                continue;
            var port = trimmed[..colon].Trim();
            if (port.Length == 0 || port.Contains(' '))
                continue;
            var description = trimmed[(colon + 1)..].Trim();
            var paren = description.IndexOf(" (type:", StringComparison.OrdinalIgnoreCase);
            if (paren > 0)
                description = description[..paren].Trim();
            devices.Add(new AudioDevice($"pulse/{sink}#{port}", DanishOutput(port, description), "pulse"));
        }

        return devices;
    }

    public static (string? MpvDevice, string? Sink, string? Port) Route(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return (null, null, null);
        var hash = stored.IndexOf('#');
        if (hash > "pulse/".Length && stored.StartsWith("pulse/", StringComparison.Ordinal))
        {
            var sink = stored["pulse/".Length..hash];
            var port = stored[(hash + 1)..];
            return ($"pulse/{sink}", sink, port);
        }

        return (stored, null, null);
    }

    public static void ApplyPort(string? sink, string? port)
    {
        if (string.IsNullOrWhiteSpace(sink) || string.IsNullOrWhiteSpace(port))
            return;
        if (sink.Contains(' ', StringComparison.Ordinal) || port.Contains(' ', StringComparison.Ordinal))
            return;
        Run("pactl", $"set-sink-port {sink} {port}");
    }

    public static IReadOnlyList<AudioDevice> QueryAudio()
    {
        var listed = ParseSinks(Run("pactl", "list sinks") ?? "");
        if (listed.Count > 0)
            return listed;

        var devices = new List<AudioDevice>();
        var pactl = Run("pactl", "list short sinks");
        if (!string.IsNullOrWhiteSpace(pactl))
        {
            foreach (var line in pactl.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    continue;
                var name = parts[1];
                devices.Add(new AudioDevice($"pulse/{name}", HumanAudio(name), "pulse"));
            }
        }

        if (devices.Count > 0)
            return devices;

        var aplay = Run("aplay", "-l");
        if (string.IsNullOrWhiteSpace(aplay))
            return devices;

        foreach (Match match in AplayPattern().Matches(aplay))
        {
            var card = match.Groups["card"].Value;
            var device = match.Groups["device"].Value;
            var label = match.Groups["label"].Value.Trim();
            devices.Add(new AudioDevice($"alsa/plughw:{card},{device}", string.IsNullOrWhiteSpace(label) ? $"Card {card} device {device}" : label, "alsa"));
        }

        return devices;
    }

    public static void WriteDisplayFile(string path, IReadOnlyList<DisplayInfo> displays, string? ui, string? playback)
    {
        var connected = displays.Where(item => item.Connected && item.Width > 0).ToList();
        var uiInfo = connected.FirstOrDefault(item => item.Name == ui);
        var hall = connected.FirstOrDefault(item => item.Name == playback);
        var lines = new List<string>
        {
            $"UI_OUTPUT={ui}",
            $"HALL_OUTPUT={playback}",
            $"UI_GEOM={Geom(uiInfo)}",
            $"HALL_GEOM={Geom(hall)}",
            $"DISPLAY_COUNT={connected.Count}"
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
    }

    private static string Geom(DisplayInfo? info) =>
        info is null ? "" : $"{info.Width}x{info.Height}+{info.X}+{info.Y}";

    private static string DanishOutput(string port, string description)
    {
        var text = $"{port} {description}";
        if (text.Contains("headphone", StringComparison.OrdinalIgnoreCase)
            || text.Contains("headset", StringComparison.OrdinalIgnoreCase)
            || text.Contains("hovedtelefon", StringComparison.OrdinalIgnoreCase))
            return "Hovedtelefonudgang";
        if (text.Contains("speaker", StringComparison.OrdinalIgnoreCase) || text.Contains("højttaler", StringComparison.OrdinalIgnoreCase))
            return "Indbyggede højttalere";
        if (text.Contains("hdmi", StringComparison.OrdinalIgnoreCase))
            return "Lyd på HDMI";
        return string.IsNullOrWhiteSpace(description) ? port : description;
    }

    private static string HumanAudio(string name)
    {
        if (name.Contains("hdmi", StringComparison.OrdinalIgnoreCase))
            return "Lyd på HDMI";
        if (name.Contains("analog", StringComparison.OrdinalIgnoreCase))
            return "Analog udgang";
        return name;
    }

    private static string? Run(string file, string args)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null)
                return null;
            var text = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(2000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* probe is best effort */ }
                return null;
            }
            return process.ExitCode == 0 ? text : null;
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(@"^(?<name>\S+)\s+(?<state>connected|disconnected)(?:\s+(?<primary>primary))?(?:\s+(?<w>\d+)x(?<h>\d+)\+(?<x>\d+)\+(?<y>\d+))?", RegexOptions.Multiline)]
    private static partial Regex OutputPattern();

    [GeneratedRegex(@"card\s+(?<card>\d+):.*?\n(?:.*\n)*?\s*device\s+(?<device>\d+):\s*(?<label>[^\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex AplayPattern();
}
