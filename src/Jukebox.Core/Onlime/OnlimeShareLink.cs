namespace Jukebox.Core.Onlime;

public sealed record OnlimeShareLink(string Host, string ShareId, string FolderPath)
{
    public string Url => FolderPath == "/"
        ? $"https://{Host}/s/{ShareId}"
        : $"https://{Host}/s/{ShareId}/list{FolderPath}";

    public static bool TryParse(string? text, out OnlimeShareLink link)
    {
        link = new OnlimeShareLink("", "", "/");
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var raw = text.Trim();
        if (!raw.Contains("://", StringComparison.Ordinal))
            raw = "https://" + raw.TrimStart('/');
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return false;
        if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort)
            return false;
        if (!AllowedHost(uri.IdnHost))
            return false;

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || !parts[0].Equals("s", StringComparison.OrdinalIgnoreCase))
            return false;
        var shareId = parts[1];
        if (shareId.Length is < 6 or > 128 || !shareId.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
            return false;

        var rest = parts.Skip(2).ToList();
        if (rest.Count > 0 && (rest[0].Equals("list", StringComparison.OrdinalIgnoreCase) || rest[0].Equals("thumbs", StringComparison.OrdinalIgnoreCase)))
            rest.RemoveAt(0);

        var folder = "/";
        if (rest.Count > 0)
        {
            var decoded = rest.Select(Uri.UnescapeDataString).ToList();
            if (decoded.Any(part => part is "" or "." or ".." || part.Contains('/') || part.Contains('\\')))
                return false;
            folder = "/" + string.Join('/', decoded);
        }

        link = new OnlimeShareLink(uri.IdnHost.ToLowerInvariant(), shareId, folder);
        return true;
    }

    public static bool AllowedHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;
        return host.Equals("onlime.dk", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".onlime.dk", StringComparison.OrdinalIgnoreCase)
            || host.Equals("jottacloud.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".jottacloud.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("jotta.cloud", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".jotta.cloud", StringComparison.OrdinalIgnoreCase);
    }
}
