using Jukebox.Core.Domain;
using Jukebox.Core.Metadata;
using Microsoft.Data.Sqlite;

namespace Jukebox.Core.Data;

public sealed class CatalogFilter
{
    public string? Text { get; init; }
    public string? CategoryId { get; init; }
    public string? Artist { get; init; }
    public string? Letter { get; init; }
    public string? MediaType { get; init; }
    public int Limit { get; init; } = 60;
    public int Offset { get; init; }
    public bool IncludeUnready { get; init; }
}

public sealed class IndexedSave
{
    public required string SourceId { get; init; }
    public required string RelativePath { get; init; }
    public required string FileName { get; init; }
    public required string MediaType { get; init; }
    public required string LibraryRole { get; init; }
    public long FileSize { get; init; }
    public long ModifiedUnix { get; init; }
    public string? SampleHash { get; init; }
    public ProbeTags? Probe { get; init; }
    public FileNameParts FileNameParts { get; init; }
    public ResolvedMetadata? Resolved { get; init; }
    public string? ThumbnailPath { get; init; }
    public bool Ready { get; init; }
    public string? Error { get; init; }
    public bool HasOverride { get; init; }
}

public sealed class MediaStore
{
    private const string Columns = """
        id, source_id, relative_path, file_name, extension, media_type, library_role,
        file_size, modified_unix, sample_hash, duration_ms, width, height, bitrate,
        video_codec, audio_codec, title, artist, album, genre, year, decade, artist_letter,
        embedded_title, embedded_artist, embedded_album, embedded_genre, embedded_year,
        filename_title, filename_artist, filename_year, has_override, is_hidden, is_ready,
        force_reindex, index_state, error_message, thumbnail_path, user_play_count, auto_play_count,
        last_user_played_at, last_auto_played_at, available_from, available_until
        """;

    private readonly JukeboxDatabase _database;

    public MediaStore(JukeboxDatabase database) => _database = database;

    public IReadOnlyList<MediaSource> ListSources()
    {
        using var connection = _database.Open();
        using var reader = JukeboxDatabase.Query(connection, "SELECT id, name, kind, path, priority, enabled, role FROM sources ORDER BY priority, name;");
        var list = new List<MediaSource>();
        while (reader.Read())
        {
            list.Add(new MediaSource
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Kind = reader.GetString(2),
                Path = reader.GetString(3),
                Priority = reader.GetInt32(4),
                Enabled = reader.GetInt64(5) != 0,
                Role = reader.GetString(6)
            });
        }

        return list;
    }

    public void SeedSources(IReadOnlyList<MediaSource> sources)
    {
        _database.InWrite((connection, tx) =>
        {
            using var count = JukeboxDatabase.Query(connection, "SELECT COUNT(*) FROM sources;", tx);
            count.Read();
            if (count.GetInt32(0) > 0)
                return;

            foreach (var source in sources)
            {
                JukeboxDatabase.Execute(connection, """
                    INSERT INTO sources(id, name, kind, path, priority, enabled, role, created_at, updated_at)
                    VALUES ($id, $name, $kind, $path, $priority, $enabled, $role, $at, $at);
                    """, tx,
                    ("$id", source.Id), ("$name", source.Name), ("$kind", source.Kind), ("$path", source.Path),
                    ("$priority", source.Priority), ("$enabled", source.Enabled ? 1 : 0), ("$role", source.Role),
                    ("$at", ClubCulture.Iso(DateTime.UtcNow)));
            }
        });
    }

    public void UpsertSource(MediaSource source)
    {
        var now = ClubCulture.Iso(DateTime.UtcNow);
        _database.InWrite((connection, tx) =>
        {
            JukeboxDatabase.Execute(connection, """
                INSERT INTO sources(id, name, kind, path, priority, enabled, role, created_at, updated_at)
                VALUES ($id, $name, $kind, $path, $priority, $enabled, $role, $at, $at)
                ON CONFLICT(id) DO UPDATE SET
                    name=excluded.name, kind=excluded.kind, path=excluded.path, priority=excluded.priority,
                    enabled=excluded.enabled, role=excluded.role, updated_at=excluded.updated_at;
                """, tx,
                ("$id", source.Id), ("$name", source.Name), ("$kind", source.Kind), ("$path", source.Path),
                ("$priority", source.Priority), ("$enabled", source.Enabled ? 1 : 0), ("$role", source.Role),
                ("$at", now));
        });
    }

    public void DeleteSource(string id) =>
        _database.InWrite((connection, tx) =>
        {
            using var reader = JukeboxDatabase.Query(connection, "SELECT id FROM media_items WHERE source_id=$id;", tx, ("$id", id));
            var ids = new List<string>();
            while (reader.Read())
                ids.Add(reader.GetString(0));
            reader.Dispose();
            foreach (var mediaId in ids)
                JukeboxDatabase.Execute(connection, "DELETE FROM media_fts WHERE media_id=$id;", tx, ("$id", mediaId));
            JukeboxDatabase.Execute(connection, "DELETE FROM sources WHERE id=$id;", tx, ("$id", id));
        });

    public MediaItem? FindByPath(string sourceId, string relativePath)
    {
        using var connection = _database.Open();
        using var reader = JukeboxDatabase.Query(connection, $"SELECT {Columns} FROM media_items WHERE source_id=$s AND relative_path=$p;", null, ("$s", sourceId), ("$p", relativePath));
        return reader.Read() ? Map(reader) : null;
    }

    public MediaItem? Get(string id)
    {
        using var connection = _database.Open();
        using var reader = JukeboxDatabase.Query(connection, $"SELECT {Columns} FROM media_items WHERE id=$id;", null, ("$id", id));
        return reader.Read() ? Map(reader) : null;
    }

    public string SaveIndexed(IndexedSave save, IReadOnlyList<CategoryDraft> categories)
    {
        var now = ClubCulture.Iso(DateTime.UtcNow);
        var resolved = save.Resolved;
        var probe = save.Probe;
        return _database.InWrite((connection, tx) =>
        {
            var id = Guid.NewGuid().ToString("N");
            JukeboxDatabase.Execute(connection, """
                INSERT INTO media_items (
                    id, source_id, relative_path, file_name, extension, media_type, library_role,
                    file_size, modified_unix, sample_hash, duration_ms, width, height, bitrate,
                    video_codec, audio_codec, title, artist, album, genre, year, decade, artist_letter,
                    embedded_title, embedded_artist, embedded_album, embedded_genre, embedded_year,
                    filename_title, filename_artist, filename_year, has_override, is_hidden, is_ready,
                    force_reindex, index_state, error_message, thumbnail_path, user_play_count, auto_play_count,
                    added_at, updated_at, indexed_at)
                VALUES (
                    $id, $source, $rel, $file, $ext, $type, $role,
                    $size, $mtime, $hash, $duration, $width, $height, $bitrate,
                    $vcodec, $acodec, $title, $artist, $album, $genre, $year, $decade, $letter,
                    $etitle, $eartist, $ealbum, $egenre, $eyear,
                    $ftitle, $fartist, $fyear, $override, 0, $ready,
                    0, $state, $error, $thumb, 0, 0,
                    $now, $now, $now)
                ON CONFLICT(source_id, relative_path) DO UPDATE SET
                    file_name=excluded.file_name, extension=excluded.extension, media_type=excluded.media_type,
                    library_role=excluded.library_role, file_size=excluded.file_size, modified_unix=excluded.modified_unix,
                    sample_hash=excluded.sample_hash, duration_ms=excluded.duration_ms, width=excluded.width,
                    height=excluded.height, bitrate=excluded.bitrate, video_codec=excluded.video_codec,
                    audio_codec=excluded.audio_codec, title=excluded.title, artist=excluded.artist, album=excluded.album,
                    genre=excluded.genre, year=excluded.year, decade=excluded.decade, artist_letter=excluded.artist_letter,
                    embedded_title=excluded.embedded_title, embedded_artist=excluded.embedded_artist,
                    embedded_album=excluded.embedded_album, embedded_genre=excluded.embedded_genre,
                    embedded_year=excluded.embedded_year, filename_title=excluded.filename_title,
                    filename_artist=excluded.filename_artist, filename_year=excluded.filename_year,
                    has_override=excluded.has_override, is_ready=excluded.is_ready, force_reindex=0,
                    index_state=excluded.index_state, error_message=excluded.error_message,
                    thumbnail_path=COALESCE(excluded.thumbnail_path, media_items.thumbnail_path),
                    updated_at=excluded.updated_at, indexed_at=excluded.indexed_at;
                """, tx,
                ("$id", id), ("$source", save.SourceId), ("$rel", save.RelativePath), ("$file", save.FileName),
                ("$ext", Path.GetExtension(save.FileName).ToLowerInvariant()), ("$type", save.MediaType), ("$role", save.LibraryRole),
                ("$size", save.FileSize), ("$mtime", save.ModifiedUnix), ("$hash", save.SampleHash),
                ("$duration", probe?.DurationMs), ("$width", probe?.Width), ("$height", probe?.Height), ("$bitrate", probe?.Bitrate),
                ("$vcodec", probe?.VideoCodec), ("$acodec", probe?.AudioCodec),
                ("$title", resolved?.Title ?? save.FileName), ("$artist", resolved?.Artist ?? ""),
                ("$album", resolved?.Album ?? ""), ("$genre", resolved?.Genre ?? ""),
                ("$year", resolved?.Year), ("$decade", resolved?.Decade), ("$letter", resolved?.ArtistLetter ?? "#"),
                ("$etitle", probe?.Title), ("$eartist", probe?.Artist), ("$ealbum", probe?.Album),
                ("$egenre", probe?.Genre), ("$eyear", probe?.Year),
                ("$ftitle", save.FileNameParts.Title), ("$fartist", save.FileNameParts.Artist), ("$fyear", save.FileNameParts.Year),
                ("$override", save.HasOverride ? 1 : 0), ("$ready", save.Ready ? 1 : 0),
                ("$state", save.Ready ? "ready" : "error"), ("$error", save.Error), ("$thumb", save.ThumbnailPath),
                ("$now", now));

            using var idReader = JukeboxDatabase.Query(connection, "SELECT id FROM media_items WHERE source_id=$s AND relative_path=$p;", tx, ("$s", save.SourceId), ("$p", save.RelativePath));
            idReader.Read();
            var actualId = idReader.GetString(0);
            idReader.Dispose();
            if (save.Ready)
                ReplaceAutoCategories(connection, tx, actualId, categories, now);
            else
                JukeboxDatabase.Execute(connection, "DELETE FROM media_fts WHERE media_id=$id;", tx, ("$id", actualId));
            WriteFts(connection, tx, actualId);
            return actualId;
        });
    }

    public void TouchFile(string id, long size, long mtime, string hash) =>
        _database.InWrite((connection, tx) => JukeboxDatabase.Execute(connection, """
            UPDATE media_items SET file_size=$size, modified_unix=$mtime, sample_hash=$hash, updated_at=$now
            WHERE id=$id;
            """, tx, ("$size", size), ("$mtime", mtime), ("$hash", hash), ("$now", ClubCulture.Iso(DateTime.UtcNow)), ("$id", id)));

    public void ApplyResolved(string id, ResolvedMetadata resolved, IReadOnlyList<CategoryDraft> categories, bool hasOverride) =>
        _database.InWrite((connection, tx) =>
        {
            var now = ClubCulture.Iso(DateTime.UtcNow);
            JukeboxDatabase.Execute(connection, """
                UPDATE media_items SET title=$title, artist=$artist, album=$album, genre=$genre, year=$year,
                    decade=$decade, artist_letter=$letter, has_override=$override, updated_at=$now
                WHERE id=$id;
                """, tx,
                ("$title", resolved.Title), ("$artist", resolved.Artist), ("$album", resolved.Album),
                ("$genre", resolved.Genre), ("$year", resolved.Year), ("$decade", resolved.Decade),
                ("$letter", resolved.ArtistLetter), ("$override", hasOverride ? 1 : 0), ("$now", now), ("$id", id));
            ReplaceAutoCategories(connection, tx, id, categories, now);
            WriteFts(connection, tx, id);
        });

    public void SetHidden(string id, bool hidden) =>
        _database.InWrite((connection, tx) =>
        {
            JukeboxDatabase.Execute(connection, "UPDATE media_items SET is_hidden=$hidden, updated_at=$now WHERE id=$id;", tx,
                ("$hidden", hidden ? 1 : 0), ("$now", ClubCulture.Iso(DateTime.UtcNow)), ("$id", id));
            if (hidden)
            {
                JukeboxDatabase.Execute(connection, "DELETE FROM queue_items WHERE media_id=$id AND status='queued';", tx, ("$id", id));
            }
        });

    public void SetWindow(string id, string? from, string? until) =>
        _database.InWrite((connection, tx) => JukeboxDatabase.Execute(connection, """
            UPDATE media_items SET available_from=$from, available_until=$until, updated_at=$now WHERE id=$id;
            """, tx, ("$from", from), ("$until", until), ("$now", ClubCulture.Iso(DateTime.UtcNow)), ("$id", id)));

    public MetadataOverride? GetOverride(string id)
    {
        using var connection = _database.Open();
        using var reader = JukeboxDatabase.Query(connection, "SELECT title, artist, album, genre, year FROM metadata_overrides WHERE media_id=$id;", null, ("$id", id));
        if (!reader.Read())
            return null;
        return new MetadataOverride
        {
            Title = reader.IsDBNull(0) ? null : reader.GetString(0),
            Artist = reader.IsDBNull(1) ? null : reader.GetString(1),
            Album = reader.IsDBNull(2) ? null : reader.GetString(2),
            Genre = reader.IsDBNull(3) ? null : reader.GetString(3),
            Year = reader.IsDBNull(4) ? null : reader.GetInt32(4)
        };
    }

    public void SaveOverride(string id, MetadataOverride value) =>
        _database.InWrite((connection, tx) => JukeboxDatabase.Execute(connection, """
            INSERT INTO metadata_overrides(media_id, title, artist, album, genre, year, updated_at)
            VALUES ($id, $title, $artist, $album, $genre, $year, $now)
            ON CONFLICT(media_id) DO UPDATE SET
                title=excluded.title, artist=excluded.artist, album=excluded.album,
                genre=excluded.genre, year=excluded.year, updated_at=excluded.updated_at;
            """, tx,
            ("$id", id), ("$title", value.Title), ("$artist", value.Artist), ("$album", value.Album),
            ("$genre", value.Genre), ("$year", value.Year), ("$now", ClubCulture.Iso(DateTime.UtcNow))));

    public void ClearOverride(string id) =>
        _database.InWrite((connection, tx) => JukeboxDatabase.Execute(connection, "DELETE FROM metadata_overrides WHERE media_id=$id;", tx, ("$id", id)));

    public void SetManualCategories(string id, IReadOnlyList<string> categoryIds)
    {
        _database.InWrite((connection, tx) =>
        {
            JukeboxDatabase.Execute(connection, "DELETE FROM media_categories WHERE media_id=$id AND origin='manual';", tx, ("$id", id));
            foreach (var categoryId in categoryIds.Distinct())
            {
                JukeboxDatabase.Execute(connection, """
                    INSERT OR IGNORE INTO media_categories(media_id, category_id, origin) VALUES ($m, $c, 'manual');
                    """, tx, ("$m", id), ("$c", categoryId));
            }
            WriteFts(connection, tx, id);
        });
    }

    public IReadOnlyList<MediaItem> Query(CatalogFilter filter)
    {
        using var connection = _database.Open();
        List<string>? ranked = null;
        if (!string.IsNullOrWhiteSpace(filter.Text))
        {
            var match = SearchSyntax.ToFts(filter.Text);
            if (match is null)
                return [];
            try
            {
                using var fts = JukeboxDatabase.Query(connection, "SELECT media_id FROM media_fts WHERE media_fts MATCH $q ORDER BY bm25(media_fts) LIMIT 200;", null, ("$q", match));
                ranked = [];
                while (fts.Read())
                    ranked.Add(fts.GetString(0));
            }
            catch (SqliteException)
            {
                return [];
            }

            if (ranked.Count == 0)
                return [];
        }

        var sql = $"SELECT {Columns} FROM media_items WHERE 1=1";
        var parameters = new List<(string, object?)>();
        if (!filter.IncludeUnready)
            sql += " AND is_ready=1 AND is_hidden=0 AND library_role='catalog'";
        if (!string.IsNullOrWhiteSpace(filter.CategoryId))
        {
            sql += " AND id IN (SELECT media_id FROM media_categories WHERE category_id=$cat)";
            parameters.Add(("$cat", filter.CategoryId));
        }
        if (!string.IsNullOrWhiteSpace(filter.Artist))
        {
            sql += " AND artist=$artist COLLATE NOCASE";
            parameters.Add(("$artist", filter.Artist));
        }
        if (!string.IsNullOrWhiteSpace(filter.Letter))
        {
            sql += " AND artist_letter=$letter";
            parameters.Add(("$letter", filter.Letter));
        }
        if (!string.IsNullOrWhiteSpace(filter.MediaType))
        {
            sql += " AND media_type=$type";
            parameters.Add(("$type", filter.MediaType));
        }
        if (ranked is not null)
        {
            var names = new List<string>();
            for (var i = 0; i < ranked.Count; i++)
            {
                var name = "$id" + i;
                names.Add(name);
                parameters.Add((name, ranked[i]));
            }
            sql += $" AND id IN ({string.Join(",", names)})";
        }
        else
        {
            sql += " ORDER BY artist COLLATE NOCASE, title COLLATE NOCASE";
            sql += " LIMIT $limit OFFSET $offset";
            parameters.Add(("$limit", Math.Clamp(filter.Limit, 1, 200)));
            parameters.Add(("$offset", Math.Max(0, filter.Offset)));
        }

        using var reader = JukeboxDatabase.Query(connection, sql + ";", null, parameters.Select(item => (item.Item1, item.Item2)).ToArray());
        var items = new List<MediaItem>();
        while (reader.Read())
            items.Add(Map(reader));

        if (ranked is null)
            return items;

        var order = ranked.Select((id, index) => (id, index)).ToDictionary(item => item.id, item => item.index);
        return items
            .OrderBy(item => order.TryGetValue(item.Id, out var index) ? index : int.MaxValue)
            .Skip(Math.Max(0, filter.Offset))
            .Take(Math.Clamp(filter.Limit, 1, 200))
            .ToList();
    }

    public IReadOnlyList<(string Letter, string Name, int Count)> Artists()
    {
        using var connection = _database.Open();
        using var reader = JukeboxDatabase.Query(connection, """
            SELECT artist_letter, artist, COUNT(*)
            FROM media_items
            WHERE is_ready=1 AND is_hidden=0 AND library_role='catalog' AND artist <> ''
            GROUP BY artist_letter, artist;
            """);
        var list = new List<(string, string, int)>();
        while (reader.Read())
            list.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        return list
            .OrderBy(item => item.Item1 == "#" ? 1 : 0)
            .ThenBy(item => item.Item2, StringComparer.Create(ClubCulture.Danish, true))
            .ToList();
    }

    public IReadOnlyList<Category> Categories(bool publicOnly)
    {
        using var connection = _database.Open();
        var sql = publicOnly
            ? """
              SELECT c.id, c.name, c.slug, c.kind, c.is_auto, c.is_hidden, COUNT(m.id)
              FROM categories c
              JOIN media_categories mc ON mc.category_id=c.id
              JOIN media_items m ON m.id=mc.media_id AND m.is_ready=1 AND m.is_hidden=0 AND m.library_role='catalog'
              WHERE c.is_hidden=0
              GROUP BY c.id
              ORDER BY c.kind, c.name;
              """
            : """
              SELECT c.id, c.name, c.slug, c.kind, c.is_auto, c.is_hidden, COUNT(m.id)
              FROM categories c
              LEFT JOIN media_categories mc ON mc.category_id=c.id
              LEFT JOIN media_items m ON m.id=mc.media_id AND m.is_ready=1 AND m.library_role='catalog'
              GROUP BY c.id
              ORDER BY c.kind, c.name;
              """;
        using var reader = JukeboxDatabase.Query(connection, sql);
        var list = new List<Category>();
        while (reader.Read())
        {
            list.Add(new Category
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Slug = reader.GetString(2),
                Kind = reader.GetString(3),
                IsAuto = reader.GetInt64(4) != 0,
                IsHidden = reader.GetInt64(5) != 0,
                Count = reader.GetInt32(6)
            });
        }

        return list;
    }

    public string AddCustomCategory(string name)
    {
        var id = Guid.NewGuid().ToString("N");
        var slug = Categorizer.Slug("custom", name);
        _database.InWrite((connection, tx) => JukeboxDatabase.Execute(connection, """
            INSERT INTO categories(id, name, slug, kind, is_auto, is_hidden, created_at)
            VALUES ($id, $name, $slug, 'custom', 0, 0, $now);
            """, tx, ("$id", id), ("$name", name.Trim()), ("$slug", slug), ("$now", ClubCulture.Iso(DateTime.UtcNow))));
        return id;
    }

    public void UpdateCategory(string id, string name, bool hidden) =>
        _database.InWrite((connection, tx) => JukeboxDatabase.Execute(connection, """
            UPDATE categories SET name=$name, is_hidden=$hidden, is_auto=0 WHERE id=$id;
            """, tx, ("$name", name.Trim()), ("$hidden", hidden ? 1 : 0), ("$id", id)));

    public void DeleteCategory(string id) =>
        _database.InWrite((connection, tx) => JukeboxDatabase.Execute(connection, "DELETE FROM categories WHERE id=$id;", tx, ("$id", id)));

    public void FoldDecadeTags() =>
        _database.InWrite((connection, tx) =>
        {
            var rows = new List<(string Id, string Name, string Slug, string Kind)>();
            using (var reader = JukeboxDatabase.Query(connection, "SELECT id, name, slug, kind FROM categories;", tx))
            {
                while (reader.Read())
                    rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }

            var groups = new Dictionary<string, List<(string Id, string Name, string Slug, string Kind)>>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var canonical = ClubCulture.CanonicalDecade(row.Name);
                if (canonical is null)
                    continue;
                if (!groups.TryGetValue(canonical, out var list))
                {
                    list = [];
                    groups[canonical] = list;
                }
                list.Add(row);
            }

            var touched = new HashSet<string>();
            foreach (var (canonical, list) in groups)
            {
                var slug = Categorizer.Slug("decade", canonical);
                var keeperIndex = list.FindIndex(item => item.Slug == slug);
                var already = list.Count == 1 && keeperIndex == 0 && list[0].Kind == "decade" && list[0].Name == canonical;
                if (already)
                    continue;

                string keeperId;
                if (keeperIndex < 0)
                {
                    keeperId = list[0].Id;
                    JukeboxDatabase.Execute(connection, """
                        UPDATE categories SET name=$name, slug=$slug, kind='decade' WHERE id=$id;
                        """, tx, ("$name", canonical), ("$slug", slug), ("$id", keeperId));
                }
                else
                {
                    keeperId = list[keeperIndex].Id;
                    if (list[keeperIndex].Name != canonical || list[keeperIndex].Kind != "decade")
                    {
                        JukeboxDatabase.Execute(connection, """
                            UPDATE categories SET name=$name, kind='decade' WHERE id=$id;
                            """, tx, ("$name", canonical), ("$id", keeperId));
                    }
                }

                foreach (var alias in list)
                {
                    if (alias.Id == keeperId)
                        continue;
                    using (var reader = JukeboxDatabase.Query(connection, "SELECT media_id FROM media_categories WHERE category_id=$id;", tx, ("$id", alias.Id)))
                    {
                        while (reader.Read())
                            touched.Add(reader.GetString(0));
                    }
                    JukeboxDatabase.Execute(connection, """
                        INSERT OR IGNORE INTO media_categories(media_id, category_id, origin)
                        SELECT media_id, $keeper, origin FROM media_categories WHERE category_id=$alias;
                        """, tx, ("$keeper", keeperId), ("$alias", alias.Id));
                    JukeboxDatabase.Execute(connection, "DELETE FROM categories WHERE id=$id;", tx, ("$id", alias.Id));
                }

                using (var reader = JukeboxDatabase.Query(connection, "SELECT media_id FROM media_categories WHERE category_id=$id;", tx, ("$id", keeperId)))
                {
                    while (reader.Read())
                        touched.Add(reader.GetString(0));
                }
            }

            foreach (var mediaId in touched)
                WriteFts(connection, tx, mediaId);
        });

    public void StripCleanTitles() =>
        _database.InWrite((connection, tx) =>
        {
            var rows = new List<(string Id, string Title)>();
            using (var reader = JukeboxDatabase.Query(connection, "SELECT id, title FROM media_items;", tx))
            {
                while (reader.Read())
                    rows.Add((reader.GetString(0), reader.GetString(1)));
            }

            var now = ClubCulture.Iso(DateTime.UtcNow);
            foreach (var (id, title) in rows)
            {
                var stripped = TitleCleaner.Strip(title);
                if (stripped.Length == 0 || stripped == title)
                    continue;
                JukeboxDatabase.Execute(connection, """
                    UPDATE media_items SET title=$title, updated_at=$now WHERE id=$id;
                    """, tx, ("$title", stripped), ("$now", now), ("$id", id));
                WriteFts(connection, tx, id);
            }
        });

    public IReadOnlyList<string> CategoryIds(string mediaId)
    {
        using var connection = _database.Open();
        using var reader = JukeboxDatabase.Query(connection, "SELECT category_id FROM media_categories WHERE media_id=$id;", null, ("$id", mediaId));
        var list = new List<string>();
        while (reader.Read())
            list.Add(reader.GetString(0));
        return list;
    }

    public IReadOnlyList<MediaItem> Events(DateTime utcNow)
    {
        var now = ClubCulture.Iso(utcNow);
        using var connection = _database.Open();
        using var reader = JukeboxDatabase.Query(connection, $"""
            SELECT {Columns} FROM media_items
            WHERE is_ready=1 AND is_hidden=0 AND library_role='event'
              AND (available_from IS NULL OR available_from <= $now)
              AND (available_until IS NULL OR available_until >= $now)
            ORDER BY file_name;
            """, null, ("$now", now));
        var list = new List<MediaItem>();
        while (reader.Read())
            list.Add(Map(reader));
        return list;
    }

    public IReadOnlyList<MediaItem> AutoplayCandidates()
    {
        using var connection = _database.Open();
        using var reader = JukeboxDatabase.Query(connection, $"""
            SELECT {Columns} FROM media_items
            WHERE is_ready=1 AND is_hidden=0 AND library_role='catalog' AND media_type IN ('video','audio')
              AND id NOT IN (SELECT media_id FROM queue_items WHERE status IN ('queued','playing'))
            ORDER BY user_play_count ASC, COALESCE(last_user_played_at, '') ASC, title
            LIMIT 40;
            """);
        var list = new List<MediaItem>();
        while (reader.Read())
            list.Add(Map(reader));
        return list;
    }

    public HashSet<string> RecentHumanPlays(DateTime cutoffUtc)
    {
        using var connection = _database.Open();
        using var reader = JukeboxDatabase.Query(connection, """
            SELECT DISTINCT media_id FROM play_history
            WHERE origin IN ('user','mobile') AND played_at >= $cut;
            """, null, ("$cut", ClubCulture.Iso(cutoffUtc)));
        var set = new HashSet<string>();
        while (reader.Read())
            set.Add(reader.GetString(0));
        return set;
    }

    public HashSet<string> ActiveQueueIds()
    {
        using var connection = _database.Open();
        using var reader = JukeboxDatabase.Query(connection, "SELECT media_id FROM queue_items WHERE status IN ('queued','playing');");
        var set = new HashSet<string>();
        while (reader.Read())
            set.Add(reader.GetString(0));
        return set;
    }

    public void RemoveUnseen(IReadOnlyCollection<string> scannedSourceIds, IReadOnlySet<string> seen)
    {
        if (scannedSourceIds.Count == 0)
            return;
        _database.InWrite((connection, tx) =>
        {
            var parameters = new List<(string, object?)>();
            var names = new List<string>();
            var index = 0;
            foreach (var sourceId in scannedSourceIds)
            {
                var name = "$s" + index++;
                names.Add(name);
                parameters.Add((name, sourceId));
            }

            using var reader = JukeboxDatabase.Query(connection, $"SELECT id, source_id, relative_path FROM media_items WHERE source_id IN ({string.Join(",", names)});", tx, parameters.ToArray());
            var doomed = new List<string>();
            while (reader.Read())
            {
                var key = reader.GetString(1) + "\u001f" + reader.GetString(2);
                if (!seen.Contains(key))
                    doomed.Add(reader.GetString(0));
            }
            reader.Dispose();
            foreach (var id in doomed)
            {
                JukeboxDatabase.Execute(connection, "DELETE FROM media_fts WHERE media_id=$id;", tx, ("$id", id));
                JukeboxDatabase.Execute(connection, "DELETE FROM media_items WHERE id=$id;", tx, ("$id", id));
            }
        });
    }

    public void MarkForReindex() =>
        _database.InWrite((connection, tx) => JukeboxDatabase.Execute(connection, "UPDATE media_items SET force_reindex=1, is_ready=0, index_state='pending';", tx));

    public void DeleteLibrary() =>
        _database.InWrite((connection, tx) =>
        {
            JukeboxDatabase.Execute(connection, "DELETE FROM media_fts;", tx);
            JukeboxDatabase.Execute(connection, "DELETE FROM media_items;", tx);
            JukeboxDatabase.Execute(connection, "DELETE FROM categories WHERE is_auto=1;", tx);
        });

    public (int Ready, int Hidden, int Errors) Counts()
    {
        using var connection = _database.Open();
        using var reader = JukeboxDatabase.Query(connection, """
            SELECT
                SUM(CASE WHEN is_ready=1 AND is_hidden=0 THEN 1 ELSE 0 END),
                SUM(CASE WHEN is_hidden=1 THEN 1 ELSE 0 END),
                SUM(CASE WHEN index_state='error' THEN 1 ELSE 0 END)
            FROM media_items;
            """);
        reader.Read();
        return (reader.IsDBNull(0) ? 0 : reader.GetInt32(0), reader.IsDBNull(1) ? 0 : reader.GetInt32(1), reader.IsDBNull(2) ? 0 : reader.GetInt32(2));
    }

    public void RecordPlay(string mediaId, string origin, bool completed, string title, string artist)
    {
        var now = ClubCulture.Iso(DateTime.UtcNow);
        _database.InWrite((connection, tx) =>
        {
            if (Origins.IsHuman(origin))
            {
                JukeboxDatabase.Execute(connection, """
                    UPDATE media_items SET user_play_count=user_play_count+1, last_user_played_at=$now WHERE id=$id;
                    """, tx, ("$now", now), ("$id", mediaId));
            }
            else
            {
                JukeboxDatabase.Execute(connection, """
                    UPDATE media_items SET auto_play_count=auto_play_count+1, last_auto_played_at=$now WHERE id=$id;
                    """, tx, ("$now", now), ("$id", mediaId));
            }

            JukeboxDatabase.Execute(connection, """
                INSERT INTO play_history(id, media_id, zone_id, played_at, origin, completed, title_snapshot, artist_snapshot)
                VALUES ($id, $media, 'main', $now, $origin, $done, $title, $artist);
                """, tx,
                ("$id", Guid.NewGuid().ToString("N")), ("$media", mediaId), ("$now", now), ("$origin", origin),
                ("$done", completed ? 1 : 0), ("$title", title), ("$artist", artist));
        });
    }

    public static string Key(string sourceId, string relativePath) => sourceId + "\u001f" + relativePath;

    private static void ReplaceAutoCategories(SqliteConnection connection, SqliteTransaction tx, string mediaId, IReadOnlyList<CategoryDraft> categories, string now)
    {
        JukeboxDatabase.Execute(connection, "DELETE FROM media_categories WHERE media_id=$id AND origin='auto';", tx, ("$id", mediaId));
        foreach (var draft in categories)
        {
            var slug = Categorizer.Slug(draft.Kind, draft.Name);
            string categoryId;
            using (var reader = JukeboxDatabase.Query(connection, "SELECT id FROM categories WHERE slug=$slug;", tx, ("$slug", slug)))
            {
                if (reader.Read())
                    categoryId = reader.GetString(0);
                else
                {
                    categoryId = Guid.NewGuid().ToString("N");
                    reader.Dispose();
                    JukeboxDatabase.Execute(connection, """
                        INSERT INTO categories(id, name, slug, kind, is_auto, is_hidden, created_at)
                        VALUES ($id, $name, $slug, $kind, 1, 0, $now);
                        """, tx, ("$id", categoryId), ("$name", draft.Name), ("$slug", slug), ("$kind", draft.Kind), ("$now", now));
                }
            }

            JukeboxDatabase.Execute(connection, """
                INSERT OR IGNORE INTO media_categories(media_id, category_id, origin) VALUES ($m, $c, 'auto');
                """, tx, ("$m", mediaId), ("$c", categoryId));
        }
    }

    private static void WriteFts(SqliteConnection connection, SqliteTransaction tx, string mediaId)
    {
        using var media = JukeboxDatabase.Query(connection, "SELECT title, artist, album, genre, is_ready FROM media_items WHERE id=$id;", tx, ("$id", mediaId));
        if (!media.Read())
            return;
        var ready = media.GetInt64(4) != 0;
        var title = media.GetString(0);
        var artist = media.GetString(1);
        var album = media.GetString(2);
        var genre = media.GetString(3);
        media.Dispose();
        JukeboxDatabase.Execute(connection, "DELETE FROM media_fts WHERE media_id=$id;", tx, ("$id", mediaId));
        if (!ready)
            return;
        using var cats = JukeboxDatabase.Query(connection, """
            SELECT c.name FROM categories c
            JOIN media_categories mc ON mc.category_id=c.id
            WHERE mc.media_id=$id;
            """, tx, ("$id", mediaId));
        var names = new List<string>();
        while (cats.Read())
            names.Add(cats.GetString(0));
        cats.Dispose();
        JukeboxDatabase.Execute(connection, """
            INSERT INTO media_fts(media_id, title, artist, album, genre, categories)
            VALUES ($id, $title, $artist, $album, $genre, $cats);
            """, tx,
            ("$id", mediaId), ("$title", title), ("$artist", artist), ("$album", album), ("$genre", genre),
            ("$cats", string.Join(' ', names)));
    }

    private static MediaItem Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        SourceId = reader.GetString(1),
        RelativePath = reader.GetString(2),
        FileName = reader.GetString(3),
        Extension = reader.GetString(4),
        MediaType = reader.GetString(5),
        LibraryRole = reader.GetString(6),
        FileSize = reader.GetInt64(7),
        ModifiedUnix = reader.GetInt64(8),
        SampleHash = Ns(reader, 9),
        DurationMs = Nl(reader, 10),
        Width = Ni(reader, 11),
        Height = Ni(reader, 12),
        Bitrate = Ni(reader, 13),
        VideoCodec = Ns(reader, 14),
        AudioCodec = Ns(reader, 15),
        Title = reader.GetString(16),
        Artist = reader.GetString(17),
        Album = reader.GetString(18),
        Genre = reader.GetString(19),
        Year = Ni(reader, 20),
        Decade = Ns(reader, 21),
        ArtistLetter = reader.GetString(22),
        EmbeddedTitle = Ns(reader, 23),
        EmbeddedArtist = Ns(reader, 24),
        EmbeddedAlbum = Ns(reader, 25),
        EmbeddedGenre = Ns(reader, 26),
        EmbeddedYear = Ni(reader, 27),
        FilenameTitle = Ns(reader, 28),
        FilenameArtist = Ns(reader, 29),
        FilenameYear = Ni(reader, 30),
        HasOverride = reader.GetInt64(31) != 0,
        IsHidden = reader.GetInt64(32) != 0,
        IsReady = reader.GetInt64(33) != 0,
        ForceReindex = reader.GetInt64(34) != 0,
        IndexState = reader.GetString(35),
        ErrorMessage = Ns(reader, 36),
        ThumbnailPath = Ns(reader, 37),
        UserPlayCount = reader.GetInt32(38),
        AutoPlayCount = reader.GetInt32(39),
        LastUserPlayedAt = Ns(reader, 40),
        LastAutoPlayedAt = Ns(reader, 41),
        AvailableFrom = Ns(reader, 42),
        AvailableUntil = Ns(reader, 43)
    };

    private static string? Ns(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static int? Ni(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetInt32(index);
    private static long? Nl(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetInt64(index);
}

public static class SearchSyntax
{
    public static string? ToFts(string raw)
    {
        var cleaned = raw.Replace("\"", " ", StringComparison.Ordinal).Replace("*", " ", StringComparison.Ordinal).Replace(":", " ", StringComparison.Ordinal);
        var tokens = cleaned.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return null;
        return string.Join(" AND ", tokens.Select(token => $"\"{token}\"*" ));
    }
}
