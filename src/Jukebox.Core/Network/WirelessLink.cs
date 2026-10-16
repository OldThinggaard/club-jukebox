namespace Jukebox.Core.Network;

public sealed record WirelessLink(string Ssid, int Signal)
{
    public static WirelessLink? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!raw.StartsWith("*:"))
                continue;
            var body = raw[2..];
            var cut = LastUnescapedColon(body);
            if (cut <= 0)
                continue;
            var ssid = Unescape(body[..cut]).Trim();
            if (ssid.Length == 0 || ssid == "--")
                continue;
            _ = int.TryParse(body[(cut + 1)..], out var signal);
            return new WirelessLink(ssid, Math.Clamp(signal, 0, 100));
        }

        return null;
    }

    private static int LastUnescapedColon(string value)
    {
        var found = -1;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\')
            {
                i++;
                continue;
            }

            if (value[i] == ':')
                found = i;
        }

        return found;
    }

    private static string Unescape(string value) =>
        value.Replace("\\:", ":", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
}
