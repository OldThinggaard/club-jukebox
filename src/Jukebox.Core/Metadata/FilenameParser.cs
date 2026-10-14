using System.Globalization;
using System.Text.RegularExpressions;

namespace Jukebox.Core.Metadata;

public readonly record struct FileNameParts(string? Artist, string? Title, int? Year);

public static partial class FilenameParser
{
    private static readonly string[] Separators = [" - ", " – ", " — ", " _ "];

    public static FileNameParts Parse(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName).Trim();
        var year = FindYear(stem);
        foreach (var separator in Separators)
        {
            var index = stem.IndexOf(separator, StringComparison.Ordinal);
            if (index <= 0 || index >= stem.Length - separator.Length)
                continue;

            var artist = Clean(stem[..index]);
            var title = StripYear(Clean(stem[(index + separator.Length)..]));
            if (string.IsNullOrWhiteSpace(title))
                continue;
            return new FileNameParts(NullIfEmpty(artist), title, year);
        }

        return new FileNameParts(null, StripYear(Clean(stem)), year);
    }

    public static int? FindYear(string text)
    {
        foreach (Match match in YearPattern().Matches(text))
        {
            if (int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is >= 1900 and <= 2099)
                return year;
        }

        return null;
    }

    private static string StripYear(string title)
    {
        var stripped = TrailingYear().Replace(title, "").Trim();
        return string.IsNullOrWhiteSpace(stripped) ? title : stripped.Trim(' ', '-', '–', '—');
    }

    private static string Clean(string value) =>
        Whitespace().Replace(value.Replace('_', ' '), " ").Trim(' ', '-', '–', '—');

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    [GeneratedRegex(@"\b(19|20)\d{2}\b")]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"[\s_\-\(\[]*(19|20)\d{2}[\)\]]*\s*$")]
    private static partial Regex TrailingYear();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

public static partial class TitleCleaner
{
    public static string Strip(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "";
        return CleanMarker().Replace(title, "").Trim();
    }

    [GeneratedRegex(@"\s*\(\s*clean\s*\).*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex CleanMarker();
}
