using System.Security.Cryptography;
using Jukebox.Core.Security;

namespace Jukebox.Core.Data;

public sealed class SessionStore
{
    private readonly JukeboxDatabase _database;

    public SessionStore(JukeboxDatabase database) => _database = database;

    public string Create(TimeSpan lifetime)
    {
        var token = PinHasher.NewToken();
        var now = DateTime.UtcNow;
        _database.InWrite((connection, tx) => JukeboxDatabase.Execute(connection, """
            INSERT INTO admin_sessions(id, token_hash, created_at, expires_at)
            VALUES ($id, $hash, $created, $expires);
            """, tx,
            ("$id", Guid.NewGuid().ToString("N")),
            ("$hash", PinHasher.Sha256(token)),
            ("$created", ClubCulture.Iso(now)),
            ("$expires", ClubCulture.Iso(now.Add(lifetime)))));
        return token;
    }

    public bool IsValid(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;
        var hash = PinHasher.Sha256(token);
        var now = ClubCulture.Iso(DateTime.UtcNow);
        using var connection = _database.Open();
        using var reader = JukeboxDatabase.Query(connection, """
            SELECT expires_at FROM admin_sessions WHERE token_hash=$hash;
            """, null, ("$hash", hash));
        if (!reader.Read())
            return false;
        return string.CompareOrdinal(reader.GetString(0), now) > 0;
    }

    public void Revoke(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return;
        var hash = PinHasher.Sha256(token);
        _database.InWrite((connection, tx) => JukeboxDatabase.Execute(connection, "DELETE FROM admin_sessions WHERE token_hash=$hash;", tx, ("$hash", hash)));
    }
}

public sealed class BackupStore
{
    private readonly JukeboxDatabase _database;

    public BackupStore(JukeboxDatabase database) => _database = database;

    public void Insert(Domain.BackupRecord record) =>
        _database.InWrite((connection, tx) => JukeboxDatabase.Execute(connection, """
            INSERT INTO backups(id, kind, path, size_bytes, created_at, note)
            VALUES ($id, $kind, $path, $size, $at, $note);
            """, tx,
            ("$id", record.Id), ("$kind", record.Kind), ("$path", record.Path),
            ("$size", record.SizeBytes), ("$at", record.CreatedAt), ("$note", record.Note)));

    public IReadOnlyList<Domain.BackupRecord> List()
    {
        using var connection = _database.Open();
        using var reader = JukeboxDatabase.Query(connection, "SELECT id, kind, path, size_bytes, created_at, note FROM backups ORDER BY created_at DESC;");
        var list = new List<Domain.BackupRecord>();
        while (reader.Read())
        {
            list.Add(new Domain.BackupRecord
            {
                Id = reader.GetString(0),
                Kind = reader.GetString(1),
                Path = reader.GetString(2),
                SizeBytes = reader.GetInt64(3),
                CreatedAt = reader.GetString(4),
                Note = reader.IsDBNull(5) ? null : reader.GetString(5)
            });
        }

        return list;
    }

    public Domain.BackupRecord? Get(string id)
    {
        return List().FirstOrDefault(item => item.Id == id);
    }

    public void Delete(string id) =>
        _database.InWrite((connection, tx) => JukeboxDatabase.Execute(connection, "DELETE FROM backups WHERE id=$id;", tx, ("$id", id)));
}
