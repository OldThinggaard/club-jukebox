using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jukebox.Core.Install;

public sealed record InstallDisk(string Name, string Path, string Size, string Model, string Transport, bool Removable);

public static partial class InstallDisks
{
    public static bool IsInstallTarget(string? path) =>
        !string.IsNullOrWhiteSpace(path) && TargetPattern().IsMatch(path);

    public static IReadOnlyList<InstallDisk> Parse(string json, string liveLabel = "JUKEBOX")
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? """{"blockdevices":[]}""" : json);
        if (!document.RootElement.TryGetProperty("blockdevices", out var devices))
            return [];

        var disks = new List<InstallDisk>();
        foreach (var device in devices.EnumerateArray())
        {
            if (!device.TryGetProperty("type", out var type) || type.GetString() != "disk")
                continue;
            var name = device.TryGetProperty("name", out var nameValue) ? nameValue.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(name))
                continue;
            if (TreeContains(device, liveLabel))
                continue;

            var path = "/dev/" + name;
            if (!IsInstallTarget(path))
                continue;

            disks.Add(new InstallDisk(
                name,
                path,
                Text(device, "size"),
                Text(device, "model"),
                Text(device, "tran"),
                device.TryGetProperty("rm", out var removable) && removable.ValueKind == JsonValueKind.True));
        }

        return disks
            .OrderBy(disk => disk.Removable)
            .ThenBy(disk => disk.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static bool TreeContains(JsonElement node, string liveLabel)
    {
        if (string.Equals(Text(node, "label"), liveLabel, StringComparison.OrdinalIgnoreCase))
            return true;
        var mount = Text(node, "mountpoint");
        if (mount.Contains("/run/live", StringComparison.Ordinal) || mount.Contains("/lib/live", StringComparison.Ordinal))
            return true;
        if (!node.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var child in children.EnumerateArray())
        {
            if (TreeContains(child, liveLabel))
                return true;
        }

        return false;
    }

    private static string Text(JsonElement node, string name)
    {
        if (!node.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return "";
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
    }

    [GeneratedRegex(@"^/dev/(sd[a-z]+|vd[a-z]+|nvme[0-9]+n[0-9]+|mmcblk[0-9]+|loop[0-9]+)$")]
    private static partial Regex TargetPattern();
}
