using System.Globalization;
using System.Text.RegularExpressions;

namespace Jukebox.Core;

public static partial class ClubCulture
{
    public static readonly CultureInfo Danish = CultureInfo.GetCultureInfo("da-DK");

    public const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZÆØÅ";

    public static string Letter(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return "#";

        var upper = artist.Trim().ToUpper(Danish);
        var ch = upper[0];
        return Alphabet.Contains(ch) ? ch.ToString() : "#";
    }

    public static string? DecadeLabel(int? year)
    {
        if (year is null or < 1900 or > 2090)
            return null;

        var decade = year.Value / 10 * 10;
        return decade >= 2000 ? $"{decade}s" : $"{decade % 100}s";
    }

    public static string? CanonicalDecade(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var match = DecadeAlias().Match(name.Trim());
        if (!match.Success)
            return null;

        var pair = int.Parse(match.Groups["pair"].Value, CultureInfo.InvariantCulture);
        var century = match.Groups["century"].Value;
        if (century.Length == 0)
            return $"{pair:00}s";

        var decade = int.Parse(century, CultureInfo.InvariantCulture) * 100 + pair;
        if (decade is < 1900 or > 2090)
            return null;

        return decade >= 2000 ? $"{decade}s" : $"{pair:00}s";
    }

    [GeneratedRegex(@"^(?<century>19|20)?(?<pair>\d0)['’‘´ʼ]?s$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DecadeAlias();

    public static string Iso(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture);

    public static DateTime ParseIso(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
