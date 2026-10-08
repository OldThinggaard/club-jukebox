using System.Globalization;

namespace Jukebox.Core;

public static class ClubCulture
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

    public static string Iso(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture);

    public static DateTime ParseIso(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
