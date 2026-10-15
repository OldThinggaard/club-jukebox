using System.Text.RegularExpressions;

namespace Jukebox.Core.Metadata;

public static partial class TitleCleaner
{
    public static string Strip(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return title?.Trim() ?? "";
        var match = CleanMark().Match(title);
        if (!match.Success)
            return title.Trim();
        var kept = title[..match.Index].Trim();
        return kept.Length == 0 ? title.Trim() : kept;
    }

    [GeneratedRegex(@"\(\s*Clean\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CleanMark();
}
