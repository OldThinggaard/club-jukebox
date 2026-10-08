using Jukebox.Core.Data;
using Jukebox.Core.Domain;
using Microsoft.Data.Sqlite;

namespace Jukebox.Core.Backup;

public sealed class BackupManager
{
    private readonly JukeboxDatabase _database;
    private readonly BackupStore _store;
    private readonly SettingsStore _settings;
    private readonly JukeboxPaths _paths;

    public BackupManager(JukeboxDatabase database, BackupStore store, SettingsStore settings, JukeboxPaths paths)
    {
        _database = database;
        _store = store;
        _settings = settings;
        _paths = paths;
    }

    public BackupRecord Create(string kind, string? note)
    {
        Directory.CreateDirectory(_paths.Backups);
        var id = Guid.NewGuid().ToString("N");
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(_paths.Backups, $"{kind}-{stamp}-{id[..8]}.db");
        CopyDatabase(path);
        var record = new BackupRecord
        {
            Id = id,
            Kind = kind,
            Path = path,
            SizeBytes = new FileInfo(path).Length,
            CreatedAt = ClubCulture.Iso(DateTime.UtcNow),
            Note = note
        };
        _store.Insert(record);
        _settings.Set(SettingKeys.LastBackupAt, record.CreatedAt);
        ApplyRetention();
        return record;
    }

    public void RunScheduled(DateTime localNow)
    {
        var settings = _settings.Get();
        if (localNow.Hour != settings.BackupHour)
            return;
        var today = localNow.ToString("yyyy-MM-dd");
        if (settings.LastBackupAt is not null && settings.LastBackupAt.StartsWith(today, StringComparison.Ordinal))
            return;

        Create("daily", "Scheduled daily backup");
        if (localNow.DayOfWeek == DayOfWeek.Sunday)
            Create("weekly", "Scheduled weekly backup");
        if (localNow.Day == 1)
            Create("monthly", "Scheduled monthly backup");
    }

    public void Restore(string id)
    {
        var record = _store.Get(id) ?? throw new InvalidOperationException("Backup not found.");
        if (!File.Exists(record.Path))
            throw new InvalidOperationException("Backup file is missing.");

        var safety = Create("pre-update", "Automatic copy before restore");
        var replacement = record.Path + ".restore";
        CopyFile(record.Path, replacement);
        using (var connection = new SqliteConnection($"Data Source={replacement}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            var result = command.ExecuteScalar()?.ToString();
            if (result != "ok")
                throw new InvalidOperationException("Backup failed the integrity check.");
        }

        SqliteConnection.ClearAllPools();
        File.Copy(replacement, _database.Paths.DatabasePath, true);
        foreach (var sidecar in new[] { _database.Paths.DatabasePath + "-wal", _database.Paths.DatabasePath + "-shm" })
        {
            if (File.Exists(sidecar))
                File.Delete(sidecar);
        }

        File.Delete(replacement);
        _ = safety;
    }

    public void Delete(string id)
    {
        var record = _store.Get(id);
        if (record is null)
            return;
        if (File.Exists(record.Path))
            File.Delete(record.Path);
        _store.Delete(id);
    }

    private void ApplyRetention()
    {
        foreach (var expired in RetentionPolicy.Expired(_store.List()))
            Delete(expired.Id);
    }

    private void CopyDatabase(string destination)
    {
        using var source = _database.Open();
        using var target = new SqliteConnection($"Data Source={destination}");
        target.Open();
        source.BackupDatabase(target);
    }

    private static void CopyFile(string source, string destination)
    {
        using var input = new SqliteConnection($"Data Source={source};Mode=ReadOnly");
        using var output = new SqliteConnection($"Data Source={destination}");
        input.Open();
        output.Open();
        input.BackupDatabase(output);
    }
}
