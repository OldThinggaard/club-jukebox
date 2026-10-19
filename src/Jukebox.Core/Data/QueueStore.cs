using Jukebox.Core.Domain;
using Jukebox.Core.Metadata;
using Jukebox.Core.Queue;

namespace Jukebox.Core.Data;

public sealed class QueueStore
{
    private readonly JukeboxDatabase _database;
    private readonly MediaStore _media;
    private readonly SettingsStore _settings;

    public QueueStore(JukeboxDatabase database, MediaStore media, SettingsStore settings)
    {
        _database = database;
        _media = media;
        _settings = settings;
    }

    public EnqueueResult TryEnqueue(string mediaId, string origin, string? clientLabel)
    {
        var settings = _settings.Get();
        return _database.InWrite((connection, tx) =>
        {
            using var mediaReader = JukeboxDatabase.Query(connection, """
                SELECT is_ready, is_hidden, library_role FROM media_items WHERE id=$id;
                """, tx, ("$id", mediaId));
            if (!mediaReader.Read())
                return EnqueueResult.Rejected(QueuePolicy.Message(QueueBlock.NotReady)!);
            var ready = mediaReader.GetInt64(0) != 0;
            var hidden = mediaReader.GetInt64(1) != 0;
            var role = mediaReader.GetString(2);
            mediaReader.Dispose();
            if (!hidden)
            {
                using var genreReader = JukeboxDatabase.Query(connection, """
                    SELECT 1 FROM media_categories mc
                    JOIN categories c ON c.id = mc.category_id
                    WHERE mc.media_id=$id AND c.is_hidden=1 AND c.kind='genre'
                    LIMIT 1;
                    """, tx, ("$id", mediaId));
                hidden = genreReader.Read();
            }

            string? playing = null;
            using (var playingReader = JukeboxDatabase.Query(connection, "SELECT media_id FROM queue_items WHERE status='playing' LIMIT 1;", tx))
            {
                if (playingReader.Read())
                    playing = playingReader.GetString(0);
            }

            var queued = false;
            using (var queuedReader = JukeboxDatabase.Query(connection, "SELECT 1 FROM queue_items WHERE media_id=$id AND status='queued' LIMIT 1;", tx, ("$id", mediaId)))
                queued = queuedReader.Read();

            var count = 0;
            using (var countReader = JukeboxDatabase.Query(connection, "SELECT COUNT(*) FROM queue_items WHERE status IN ('queued','playing');", tx))
            {
                countReader.Read();
                count = countReader.GetInt32(0);
            }

            var recent = false;
            if (Origins.IsHuman(origin) && settings.CooldownMinutes > 0)
            {
                var cutoff = ClubCulture.Iso(DateTime.UtcNow.AddMinutes(-settings.CooldownMinutes));
                using var recentReader = JukeboxDatabase.Query(connection, """
                    SELECT 1 FROM play_history
                    WHERE media_id=$id AND origin IN ('user','mobile') AND played_at >= $cut
                    LIMIT 1;
                    """, tx, ("$id", mediaId), ("$cut", cutoff));
                recent = recentReader.Read();
            }

            var playable = role == LibraryRoles.Catalog || (role == LibraryRoles.Event && !Origins.IsHuman(origin));
            var block = QueuePolicy.Evaluate(ready, hidden, playable, playing == mediaId, queued, recent, count >= settings.MaxQueueLength, Origins.IsHuman(origin));
            if (block != QueueBlock.None)
                return EnqueueResult.Rejected(QueuePolicy.Message(block)!);

            var id = Guid.NewGuid().ToString("N");
            JukeboxDatabase.Execute(connection, """
                INSERT INTO queue_items(id, media_id, zone_id, enqueued_at, origin, status, client_label)
                VALUES ($id, $media, 'main', $at, $origin, 'queued', $client);
                """, tx,
                ("$id", id), ("$media", mediaId), ("$at", ClubCulture.Iso(DateTime.UtcNow)),
                ("$origin", origin), ("$client", clientLabel));
            return EnqueueResult.Accepted(id);
        });
    }

    public IReadOnlyList<QueueEntry> Active()
    {
        using var connection = _database.Open();
        using var reader = JukeboxDatabase.Query(connection, """
            SELECT q.id, q.media_id, q.origin, q.status, q.enqueued_at, q.client_label,
                   m.title, m.artist, m.media_type, m.thumbnail_path, m.duration_ms, m.album
            FROM queue_items q
            JOIN media_items m ON m.id = q.media_id
            WHERE q.status IN ('queued','playing')
            ORDER BY CASE q.status WHEN 'playing' THEN 0 ELSE 1 END, q.enqueued_at;
            """);
        var list = new List<QueueEntry>();
        while (reader.Read())
        {
            list.Add(new QueueEntry
            {
                Id = reader.GetString(0),
                MediaId = reader.GetString(1),
                Origin = reader.GetString(2),
                Status = reader.GetString(3),
                EnqueuedAt = reader.GetString(4),
                ClientLabel = reader.IsDBNull(5) ? null : reader.GetString(5),
                Title = TitleCleaner.Strip(reader.GetString(6)),
                Artist = reader.GetString(7),
                MediaType = reader.GetString(8),
                ThumbnailPath = reader.IsDBNull(9) ? null : reader.GetString(9),
                DurationMs = reader.IsDBNull(10) ? null : reader.GetInt64(10),
                Album = reader.GetString(11)
            });
        }

        return list;
    }

    public QueueEntry? ClaimOldest() => ClaimNextQueued(humanOnly: false);

    /// <summary>
    /// The oldest song a person chose. A house song must not be claimed here,
    /// so a guest pick is what replaces the one the system started.
    /// </summary>
    public QueueEntry? ClaimOldestHuman() => ClaimNextQueued(humanOnly: true);

    private QueueEntry? ClaimNextQueued(bool humanOnly)
    {
        return _database.InWrite((connection, tx) =>
        {
            using var reader = JukeboxDatabase.Query(connection, """
                SELECT q.id FROM queue_items q
                WHERE q.status='queued'
                  AND ($human = 0 OR q.origin IN ('user','mobile'))
                ORDER BY q.enqueued_at
                LIMIT 1;
                """, tx, ("$human", humanOnly ? 1 : 0));
            if (!reader.Read())
                return null;
            var id = reader.GetString(0);
            reader.Dispose();
            var now = ClubCulture.Iso(DateTime.UtcNow);
            JukeboxDatabase.Execute(connection, """
                UPDATE queue_items SET status='playing', started_at=$now WHERE id=$id;
                """, tx, ("$now", now), ("$id", id));
            using var loaded = JukeboxDatabase.Query(connection, """
                SELECT q.id, q.media_id, q.origin, q.status, q.enqueued_at, q.client_label,
                       m.title, m.artist, m.media_type, m.thumbnail_path, m.duration_ms, m.album
                FROM queue_items q JOIN media_items m ON m.id=q.media_id WHERE q.id=$id;
                """, tx, ("$id", id));
            if (!loaded.Read())
                return null;
            return new QueueEntry
            {
                Id = loaded.GetString(0),
                MediaId = loaded.GetString(1),
                Origin = loaded.GetString(2),
                Status = loaded.GetString(3),
                EnqueuedAt = loaded.GetString(4),
                ClientLabel = loaded.IsDBNull(5) ? null : loaded.GetString(5),
                Title = TitleCleaner.Strip(loaded.GetString(6)),
                Artist = loaded.GetString(7),
                MediaType = loaded.GetString(8),
                ThumbnailPath = loaded.IsDBNull(9) ? null : loaded.GetString(9),
                DurationMs = loaded.IsDBNull(10) ? null : loaded.GetInt64(10),
                Album = loaded.GetString(11)
            };
        });
    }

    public void Complete(string queueId, string status) =>
        _database.InWrite((connection, tx) => JukeboxDatabase.Execute(connection, """
            UPDATE queue_items SET status=$status, finished_at=$now WHERE id=$id;
            """, tx, ("$status", status), ("$now", ClubCulture.Iso(DateTime.UtcNow)), ("$id", queueId)));

    public bool IsPlaying(string mediaId)
    {
        using var connection = _database.Open();
        using var reader = JukeboxDatabase.Query(connection, "SELECT 1 FROM queue_items WHERE media_id=$id AND status='playing' LIMIT 1;", null, ("$id", mediaId));
        return reader.Read();
    }

    public void ClearWaiting() =>
        _database.InWrite((connection, tx) => JukeboxDatabase.Execute(connection, "DELETE FROM queue_items WHERE status='queued';", tx));

    public void ClearAll() =>
        _database.InWrite((connection, tx) => JukeboxDatabase.Execute(connection, "DELETE FROM queue_items WHERE status IN ('queued','playing');", tx));

    public int ActiveCount()
    {
        using var connection = _database.Open();
        using var reader = JukeboxDatabase.Query(connection, "SELECT COUNT(*) FROM queue_items WHERE status IN ('queued','playing');");
        reader.Read();
        return reader.GetInt32(0);
    }

    public void ResetInterrupted() =>
        _database.InWrite((connection, tx) => JukeboxDatabase.Execute(connection, """
            UPDATE queue_items SET status='queued', started_at=NULL WHERE status='playing';
            """, tx));

    public MediaStore Media => _media;
}
