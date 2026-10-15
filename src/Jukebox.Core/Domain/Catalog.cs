namespace Jukebox.Core.Domain;

public static class MediaTypes
{
    public const string Video = "video";
    public const string Audio = "audio";
    public const string Image = "image";

    public static readonly IReadOnlyDictionary<string, string> ByExtension =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".mp4"] = Video,
            [".mkv"] = Video,
            [".avi"] = Video,
            [".mov"] = Video,
            [".webm"] = Video,
            [".mp3"] = Audio,
            [".aac"] = Audio,
            [".m4a"] = Audio,
            [".flac"] = Audio,
            [".wav"] = Audio,
            [".ogg"] = Audio,
            [".jpg"] = Image,
            [".jpeg"] = Image,
            [".png"] = Image,
            [".webp"] = Image
        };

    public static bool TryGet(string path, out string mediaType)
    {
        var ext = Path.GetExtension(path);
        return ByExtension.TryGetValue(ext, out mediaType!);
    }
}

public static class LibraryRoles
{
    public const string Catalog = "catalog";
    public const string Event = "event";
}

public static class Origins
{
    public const string User = "user";
    public const string Mobile = "mobile";
    public const string Auto = "auto";

    public static bool IsHuman(string origin) => origin is User or Mobile;
}

public static class SettingKeys
{
    public const string PinHash = "admin.pin_hash";
    public const string CooldownMinutes = "playback.cooldown_minutes";
    public const string IdleTimeoutSeconds = "idle.timeout_seconds";
    public const string PauseSeconds = "playback.pause_seconds";
    public const string IdleStrategy = "idle.strategy";
    public const string ClubName = "club.name";
    public const string ShowClubNameOnHall = "club.show_name_on_hall";
    public const string ShowQrOnHall = "hall.show_qr";
    public const string MaxQueueLength = "queue.max_length";
    public const string WelcomeMessage = "club.welcome_message";
    public const string Instructions = "club.instructions";
    public const string PublicBaseUrl = "qr.public_url";
    public const string AudioDevice = "audio.device";
    public const string VolumePercent = "audio.volume";
    public const string UiOutput = "display.ui_output";
    public const string PlaybackOutput = "display.playback_output";
    public const string ImageSeconds = "playback.image_seconds";
    public const string Timezone = "club.timezone";
    public const string BackupHour = "backup.hour_local";
    public const string LastScanAt = "system.last_scan_at";
    public const string LastBackupAt = "system.last_backup_at";
}

public sealed class MediaSource
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required string Path { get; init; }
    public int Priority { get; init; }
    public bool Enabled { get; init; }
    public required string Role { get; init; }
}

public sealed class MediaItem
{
    public required string Id { get; init; }
    public required string SourceId { get; init; }
    public required string RelativePath { get; init; }
    public required string FileName { get; init; }
    public required string Extension { get; init; }
    public required string MediaType { get; init; }
    public required string LibraryRole { get; init; }
    public long FileSize { get; init; }
    public long ModifiedUnix { get; init; }
    public string? SampleHash { get; init; }
    public long? DurationMs { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? Bitrate { get; init; }
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
    public required string Title { get; init; }
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string Genre { get; init; } = "";
    public int? Year { get; init; }
    public string? Decade { get; init; }
    public string ArtistLetter { get; init; } = "#";
    public string? EmbeddedTitle { get; init; }
    public string? EmbeddedArtist { get; init; }
    public string? EmbeddedAlbum { get; init; }
    public string? EmbeddedGenre { get; init; }
    public int? EmbeddedYear { get; init; }
    public string? FilenameTitle { get; init; }
    public string? FilenameArtist { get; init; }
    public int? FilenameYear { get; init; }
    public bool HasOverride { get; init; }
    public bool IsHidden { get; init; }
    public bool IsReady { get; init; }
    public bool ForceReindex { get; init; }
    public required string IndexState { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ThumbnailPath { get; init; }
    public int UserPlayCount { get; init; }
    public int AutoPlayCount { get; init; }
    public string? LastUserPlayedAt { get; init; }
    public string? LastAutoPlayedAt { get; init; }
    public string? AvailableFrom { get; init; }
    public string? AvailableUntil { get; init; }
}

public sealed class MetadataOverride
{
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? Genre { get; init; }
    public int? Year { get; init; }
}

public sealed class Category
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Slug { get; init; }
    public required string Kind { get; init; }
    public bool IsAuto { get; init; }
    public bool IsHidden { get; init; }
    public int Count { get; init; }
}

public sealed record CategoryDraft(string Name, string Kind);

public sealed class QueueEntry
{
    public required string Id { get; init; }
    public required string MediaId { get; init; }
    public required string Origin { get; init; }
    public required string Status { get; init; }
    public required string EnqueuedAt { get; init; }
    public string? ClientLabel { get; init; }
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string MediaType { get; init; } = "";
    public string? ThumbnailPath { get; init; }
    public long? DurationMs { get; init; }
    public string Album { get; init; } = "";
}

public sealed class EnqueueResult
{
    public bool Ok { get; init; }
    public string? Reason { get; init; }
    public string? QueueId { get; init; }

    public static EnqueueResult Accepted(string id) => new() { Ok = true, QueueId = id };
    public static EnqueueResult Rejected(string reason) => new() { Ok = false, Reason = reason };
}

public sealed class BackupRecord
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Path { get; init; }
    public long SizeBytes { get; init; }
    public required string CreatedAt { get; init; }
    public string? Note { get; init; }
}
