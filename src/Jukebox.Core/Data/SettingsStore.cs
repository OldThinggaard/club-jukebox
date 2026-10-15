using Jukebox.Core.Domain;
using Jukebox.Core.Queue;
using Jukebox.Core.Security;

namespace Jukebox.Core.Data;

public sealed class ClubSettings
{
    public string ClubName { get; init; } = "Oak & Iron Clubhouse";
    public bool ShowClubNameOnHall { get; init; }
    public bool ShowQrOnHall { get; init; } = true;
    public int MaxQueueLength { get; init; } = QueuePolicy.MaxQueueLength;
    public string WelcomeMessage { get; init; } = "Welcome. Choose a film on the touchscreen, or scan the code with your phone.";
    public string Instructions { get; init; } = "The queue plays in order. A film cannot be skipped, moved, or removed. You can pick it again after the cooldown.";
    public string PublicBaseUrl { get; init; } = "http://jukebox.local:47123";
    public int CooldownMinutes { get; init; } = 30;
    public int IdleTimeoutSeconds { get; init; } = 90;
    public int PauseSeconds { get; init; } = 90;
    public string IdleStrategy { get; init; } = "hybrid";
    public string AudioDevice { get; init; } = "";
    public int VolumePercent { get; init; } = 80;
    public string UiOutput { get; init; } = "";
    public string PlaybackOutput { get; init; } = "";
    public int ImageSeconds { get; init; } = 12;
    public string Timezone { get; init; } = "Europe/Copenhagen";
    public int BackupHour { get; init; } = 3;
    public string? LastScanAt { get; init; }
    public string? LastBackupAt { get; init; }
}

public sealed class SettingsStore
{
    private readonly JukeboxDatabase _database;

    public SettingsStore(JukeboxDatabase database) => _database = database;

    public void EnsureDefaults(ClubSettings defaults, string defaultPin)
    {
        _database.InWrite((connection, tx) =>
        {
            PutIfMissing(connection, tx, SettingKeys.PinHash, PinHasher.Hash(defaultPin));
            PutIfMissing(connection, tx, SettingKeys.CooldownMinutes, defaults.CooldownMinutes.ToString());
            PutIfMissing(connection, tx, SettingKeys.IdleTimeoutSeconds, defaults.IdleTimeoutSeconds.ToString());
            PutIfMissing(connection, tx, SettingKeys.PauseSeconds, defaults.PauseSeconds.ToString());
            PutIfMissing(connection, tx, SettingKeys.IdleStrategy, defaults.IdleStrategy);
            PutIfMissing(connection, tx, SettingKeys.ClubName, defaults.ClubName);
            PutIfMissing(connection, tx, SettingKeys.ShowClubNameOnHall, defaults.ShowClubNameOnHall ? "1" : "0");
            PutIfMissing(connection, tx, SettingKeys.ShowQrOnHall, defaults.ShowQrOnHall ? "1" : "0");
            PutIfMissing(connection, tx, SettingKeys.MaxQueueLength, defaults.MaxQueueLength.ToString());
            PutIfMissing(connection, tx, SettingKeys.WelcomeMessage, defaults.WelcomeMessage);
            PutIfMissing(connection, tx, SettingKeys.Instructions, defaults.Instructions);
            PutIfMissing(connection, tx, SettingKeys.PublicBaseUrl, defaults.PublicBaseUrl);
            PutIfMissing(connection, tx, SettingKeys.AudioDevice, defaults.AudioDevice);
            PutIfMissing(connection, tx, SettingKeys.VolumePercent, defaults.VolumePercent.ToString());
            PutIfMissing(connection, tx, SettingKeys.UiOutput, defaults.UiOutput);
            PutIfMissing(connection, tx, SettingKeys.PlaybackOutput, defaults.PlaybackOutput);
            PutIfMissing(connection, tx, SettingKeys.ImageSeconds, defaults.ImageSeconds.ToString());
            PutIfMissing(connection, tx, SettingKeys.Timezone, defaults.Timezone);
            PutIfMissing(connection, tx, SettingKeys.BackupHour, defaults.BackupHour.ToString());
        });
    }

    public ClubSettings Get()
    {
        var map = All();
        return new ClubSettings
        {
            ClubName = Get(map, SettingKeys.ClubName, "Oak & Iron Clubhouse"),
            ShowClubNameOnHall = Flag(map, SettingKeys.ShowClubNameOnHall, false),
            ShowQrOnHall = Flag(map, SettingKeys.ShowQrOnHall, true),
            MaxQueueLength = Math.Clamp(Int(map, SettingKeys.MaxQueueLength, QueuePolicy.MaxQueueLength), 1, QueuePolicy.MaxQueueLength),
            WelcomeMessage = Get(map, SettingKeys.WelcomeMessage, ""),
            Instructions = Get(map, SettingKeys.Instructions, ""),
            PublicBaseUrl = Get(map, SettingKeys.PublicBaseUrl, "http://jukebox.local:47123"),
            CooldownMinutes = Int(map, SettingKeys.CooldownMinutes, 30),
            IdleTimeoutSeconds = Int(map, SettingKeys.IdleTimeoutSeconds, 90),
            PauseSeconds = Int(map, SettingKeys.PauseSeconds, 90),
            IdleStrategy = Get(map, SettingKeys.IdleStrategy, "hybrid"),
            AudioDevice = Get(map, SettingKeys.AudioDevice, ""),
            VolumePercent = Int(map, SettingKeys.VolumePercent, 80),
            UiOutput = Get(map, SettingKeys.UiOutput, ""),
            PlaybackOutput = Get(map, SettingKeys.PlaybackOutput, ""),
            ImageSeconds = Int(map, SettingKeys.ImageSeconds, 12),
            Timezone = Get(map, SettingKeys.Timezone, "Europe/Copenhagen"),
            BackupHour = Int(map, SettingKeys.BackupHour, 3),
            LastScanAt = map.TryGetValue(SettingKeys.LastScanAt, out var scan) ? scan : null,
            LastBackupAt = map.TryGetValue(SettingKeys.LastBackupAt, out var backup) ? backup : null
        };
    }

    public string? GetPinHash() => All().TryGetValue(SettingKeys.PinHash, out var hash) ? hash : null;

    public void Save(ClubSettings settings)
    {
        var pairs = new Dictionary<string, string>
        {
            [SettingKeys.ClubName] = settings.ClubName.Trim(),
            [SettingKeys.ShowClubNameOnHall] = settings.ShowClubNameOnHall ? "1" : "0",
            [SettingKeys.ShowQrOnHall] = settings.ShowQrOnHall ? "1" : "0",
            [SettingKeys.MaxQueueLength] = Math.Clamp(settings.MaxQueueLength, 1, QueuePolicy.MaxQueueLength).ToString(),
            [SettingKeys.WelcomeMessage] = settings.WelcomeMessage.Trim(),
            [SettingKeys.Instructions] = settings.Instructions.Trim(),
            [SettingKeys.PublicBaseUrl] = settings.PublicBaseUrl.Trim(),
            [SettingKeys.CooldownMinutes] = Math.Clamp(settings.CooldownMinutes, 0, 24 * 60).ToString(),
            [SettingKeys.IdleTimeoutSeconds] = Math.Clamp(settings.IdleTimeoutSeconds, 0, 24 * 3600).ToString(),
            [SettingKeys.PauseSeconds] = Math.Clamp(settings.PauseSeconds, 0, 24 * 3600).ToString(),
            [SettingKeys.IdleStrategy] = settings.IdleStrategy is "random" or "least" or "hybrid" ? settings.IdleStrategy : "hybrid",
            [SettingKeys.AudioDevice] = settings.AudioDevice.Trim(),
            [SettingKeys.VolumePercent] = Math.Clamp(settings.VolumePercent, 0, 100).ToString(),
            [SettingKeys.UiOutput] = settings.UiOutput.Trim(),
            [SettingKeys.PlaybackOutput] = settings.PlaybackOutput.Trim(),
            [SettingKeys.ImageSeconds] = Math.Clamp(settings.ImageSeconds, 3, 120).ToString(),
            [SettingKeys.Timezone] = settings.Timezone.Trim(),
            [SettingKeys.BackupHour] = Math.Clamp(settings.BackupHour, 0, 23).ToString()
        };
        _database.InWrite((connection, tx) =>
        {
            foreach (var pair in pairs)
                Put(connection, tx, pair.Key, pair.Value);
        });
    }

    public void SetPin(string hash) => Set(SettingKeys.PinHash, hash);

    public void Set(string key, string value) =>
        _database.InWrite((connection, tx) => Put(connection, tx, key, value));

    public void Audit(string action, string? detail) =>
        _database.InWrite((connection, tx) => JukeboxDatabase.Execute(connection, """
            INSERT INTO audit_log(at, action, detail) VALUES ($at, $action, $detail);
            """, tx, ("$at", ClubCulture.Iso(DateTime.UtcNow)), ("$action", action), ("$detail", detail)));

    public IReadOnlyList<(string At, string Action, string? Detail)> RecentAudit(int limit)
    {
        using var connection = _database.Open();
        using var reader = JukeboxDatabase.Query(connection, "SELECT at, action, detail FROM audit_log ORDER BY id DESC LIMIT $limit;", null, ("$limit", limit));
        var list = new List<(string, string, string?)>();
        while (reader.Read())
            list.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        return list;
    }

    private Dictionary<string, string> All()
    {
        using var connection = _database.Open();
        using var reader = JukeboxDatabase.Query(connection, "SELECT key, value FROM settings;");
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
            map[reader.GetString(0)] = reader.GetString(1);
        return map;
    }

    private static void PutIfMissing(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction tx, string key, string value)
    {
        using var reader = JukeboxDatabase.Query(connection, "SELECT 1 FROM settings WHERE key=$key;", tx, ("$key", key));
        if (reader.Read())
            return;
        reader.Dispose();
        Put(connection, tx, key, value);
    }

    private static void Put(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction tx, string key, string value) =>
        JukeboxDatabase.Execute(connection, """
            INSERT INTO settings(key, value, updated_at) VALUES ($key, $value, $now)
            ON CONFLICT(key) DO UPDATE SET value=excluded.value, updated_at=excluded.updated_at;
            """, tx, ("$key", key), ("$value", value), ("$now", ClubCulture.Iso(DateTime.UtcNow)));

    private static string Get(Dictionary<string, string> map, string key, string fallback) =>
        map.TryGetValue(key, out var value) ? value : fallback;

    private static int Int(Dictionary<string, string> map, string key, int fallback) =>
        map.TryGetValue(key, out var value) && int.TryParse(value, out var number) ? number : fallback;

    private static bool Flag(Dictionary<string, string> map, string key, bool fallback)
    {
        if (!map.TryGetValue(key, out var value))
            return fallback;
        return value is "1" or "true" or "True" or "yes";
    }
}
