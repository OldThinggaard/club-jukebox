using System.Text.RegularExpressions;
using Jukebox.Core.Domain;

namespace Jukebox.Core.Metadata;

public static partial class Categorizer
{
    public static IReadOnlyList<CategoryDraft> Build(string mediaType, string? genre, int? year, string title, string artist, string album)
    {
        var list = new List<CategoryDraft>();
        list.Add(new CategoryDraft(mediaType switch
        {
            MediaTypes.Video => "Music Videos",
            MediaTypes.Audio => "Audio",
            MediaTypes.Image => "Photos",
            _ => "Other"
        }, "media_type"));

        var decade = ClubCulture.DecadeLabel(year);
        if (decade is not null)
            list.Add(new CategoryDraft(decade, "decade"));

        if (!string.IsNullOrWhiteSpace(genre))
        {
            foreach (var part in GenreSplit().Split(genre))
            {
                var name = part.Trim();
                if (name.Length == 0)
                    continue;
                var decadeName = ClubCulture.CanonicalDecade(name);
                if (decadeName is not null)
                {
                    list.Add(new CategoryDraft(decadeName, "decade"));
                    continue;
                }
                list.Add(new CategoryDraft(DanishName(name), "genre"));
            }
        }

        var blob = $"{title} {album} {artist} {genre}";
        if (LivePattern().IsMatch(blob))
            list.Add(new CategoryDraft("Live Concerts", "rule"));
        // Same chip as a genre named Danish. A second kind showed the same song twice.
        if (DanishPattern().IsMatch(blob))
            list.Add(new CategoryDraft("Danish", "genre"));

        return list
            .GroupBy(item => item.Kind + "|" + item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    public static string Slug(string kind, string name)
    {
        var lower = name.Trim().ToLower(ClubCulture.Danish);
        var chars = lower.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        var collapsed = DashRun().Replace(new string(chars), "-").Trim('-');
        if (collapsed.Length == 0)
            collapsed = "item";
        return $"{kind}-{collapsed}";
    }

    private static string TitleCase(string value) =>
        ClubCulture.Danish.TextInfo.ToTitleCase(value.ToLower(ClubCulture.Danish));

    private static string DanishName(string value)
    {
        var titled = TitleCase(value);
        return titled.Equals("Dansk", StringComparison.OrdinalIgnoreCase)
            || titled.Equals("Danish", StringComparison.OrdinalIgnoreCase)
            ? "Danish"
            : titled;
    }

    [GeneratedRegex(@"[;,/|]+")]
    private static partial Regex GenreSplit();

    [GeneratedRegex(@"\blive\b", RegexOptions.IgnoreCase)]
    private static partial Regex LivePattern();

    [GeneratedRegex(@"\b(dansk|danish)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DanishPattern();

    [GeneratedRegex("-{2,}")]
    private static partial Regex DashRun();
}
