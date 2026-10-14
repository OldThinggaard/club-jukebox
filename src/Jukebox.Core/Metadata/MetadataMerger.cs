namespace Jukebox.Core.Metadata;

public sealed class ProbeTags
{
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? Genre { get; init; }
    public int? Year { get; init; }
    public long? DurationMs { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? Bitrate { get; init; }
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
}

public sealed class ResolvedMetadata
{
    public required string Title { get; init; }
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string Genre { get; init; } = "";
    public int? Year { get; init; }
    public string? Decade { get; init; }
    public string ArtistLetter { get; init; } = "#";
}

public static class MetadataMerger
{
    public static ResolvedMetadata Resolve(ProbeTags embedded, FileNameParts fileName, Domain.MetadataOverride? @override, string originalFileName)
    {
        var stem = Path.GetFileNameWithoutExtension(originalFileName).Trim();
        var title = First(
            @override?.Title,
            SensibleTitle(embedded.Title, stem),
            fileName.Title,
            stem);
        var artist = First(@override?.Artist, embedded.Artist, fileName.Artist) ?? "";
        var album = First(@override?.Album, embedded.Album) ?? "";
        var genre = First(@override?.Genre, embedded.Genre) ?? "";
        int? year = @override is not null ? @override.Year : embedded.Year ?? fileName.Year;

        title = TitleCleaner.Strip(string.IsNullOrWhiteSpace(title) ? stem : title);
        if (string.IsNullOrWhiteSpace(title))
            title = TitleCleaner.Strip(stem);
        if (string.IsNullOrWhiteSpace(title))
            title = stem;
        artist = artist.Trim();
        return new ResolvedMetadata
        {
            Title = title,
            Artist = artist,
            Album = album.Trim(),
            Genre = genre.Trim(),
            Year = year,
            Decade = ClubCulture.DecadeLabel(year),
            ArtistLetter = ClubCulture.Letter(artist)
        };
    }

    public static ResolvedMetadata ResolveStored(Domain.MediaItem item, Domain.MetadataOverride? @override)
    {
        var embedded = new ProbeTags
        {
            Title = item.EmbeddedTitle,
            Artist = item.EmbeddedArtist,
            Album = item.EmbeddedAlbum,
            Genre = item.EmbeddedGenre,
            Year = item.EmbeddedYear
        };
        var fileName = new FileNameParts(item.FilenameArtist, item.FilenameTitle, item.FilenameYear);
        return Resolve(embedded, fileName, @override, item.FileName);
    }

    private static string? SensibleTitle(string? embeddedTitle, string stem)
    {
        if (string.IsNullOrWhiteSpace(embeddedTitle))
            return null;
        var trimmed = embeddedTitle.Trim();
        if (trimmed.Equals(stem, StringComparison.OrdinalIgnoreCase))
            return null;
        if (trimmed.Equals(stem + Path.GetExtension(stem), StringComparison.OrdinalIgnoreCase))
            return null;
        return trimmed;
    }

    private static string? First(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }
}
