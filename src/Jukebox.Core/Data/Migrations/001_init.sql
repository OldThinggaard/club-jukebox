CREATE TABLE sources (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    kind TEXT NOT NULL,
    path TEXT NOT NULL,
    priority INTEGER NOT NULL,
    enabled INTEGER NOT NULL DEFAULT 1,
    role TEXT NOT NULL DEFAULT 'catalog',
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE TABLE media_items (
    id TEXT PRIMARY KEY,
    source_id TEXT NOT NULL REFERENCES sources(id) ON DELETE CASCADE,
    relative_path TEXT NOT NULL,
    file_name TEXT NOT NULL,
    extension TEXT NOT NULL,
    media_type TEXT NOT NULL,
    library_role TEXT NOT NULL,
    file_size INTEGER NOT NULL,
    modified_unix INTEGER NOT NULL,
    sample_hash TEXT,
    duration_ms INTEGER,
    width INTEGER,
    height INTEGER,
    bitrate INTEGER,
    video_codec TEXT,
    audio_codec TEXT,
    title TEXT NOT NULL,
    artist TEXT NOT NULL DEFAULT '',
    album TEXT NOT NULL DEFAULT '',
    genre TEXT NOT NULL DEFAULT '',
    year INTEGER,
    decade TEXT,
    artist_letter TEXT NOT NULL DEFAULT '#',
    embedded_title TEXT,
    embedded_artist TEXT,
    embedded_album TEXT,
    embedded_genre TEXT,
    embedded_year INTEGER,
    filename_title TEXT,
    filename_artist TEXT,
    filename_year INTEGER,
    has_override INTEGER NOT NULL DEFAULT 0,
    is_hidden INTEGER NOT NULL DEFAULT 0,
    is_ready INTEGER NOT NULL DEFAULT 0,
    force_reindex INTEGER NOT NULL DEFAULT 0,
    index_state TEXT NOT NULL DEFAULT 'pending',
    error_message TEXT,
    thumbnail_path TEXT,
    user_play_count INTEGER NOT NULL DEFAULT 0,
    auto_play_count INTEGER NOT NULL DEFAULT 0,
    last_user_played_at TEXT,
    last_auto_played_at TEXT,
    available_from TEXT,
    available_until TEXT,
    added_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    indexed_at TEXT,
    UNIQUE (source_id, relative_path)
);

CREATE INDEX ix_media_catalog ON media_items (is_ready, is_hidden, library_role, media_type);
CREATE INDEX ix_media_artist ON media_items (artist_letter, artist);
CREATE INDEX ix_media_plays ON media_items (user_play_count, last_user_played_at);

CREATE TABLE metadata_overrides (
    media_id TEXT PRIMARY KEY REFERENCES media_items(id) ON DELETE CASCADE,
    title TEXT,
    artist TEXT,
    album TEXT,
    genre TEXT,
    year INTEGER,
    updated_at TEXT NOT NULL
);

CREATE TABLE categories (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    slug TEXT NOT NULL UNIQUE,
    kind TEXT NOT NULL,
    is_auto INTEGER NOT NULL DEFAULT 1,
    is_hidden INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL
);

CREATE TABLE media_categories (
    media_id TEXT NOT NULL REFERENCES media_items(id) ON DELETE CASCADE,
    category_id TEXT NOT NULL REFERENCES categories(id) ON DELETE CASCADE,
    origin TEXT NOT NULL DEFAULT 'auto',
    PRIMARY KEY (media_id, category_id)
);

CREATE INDEX ix_media_categories_category ON media_categories (category_id);

CREATE VIRTUAL TABLE media_fts USING fts5(
    media_id UNINDEXED,
    title,
    artist,
    album,
    genre,
    categories,
    tokenize = 'unicode61 remove_diacritics 0'
);

CREATE TABLE queue_items (
    id TEXT PRIMARY KEY,
    media_id TEXT NOT NULL REFERENCES media_items(id) ON DELETE CASCADE,
    zone_id TEXT NOT NULL DEFAULT 'main',
    enqueued_at TEXT NOT NULL,
    origin TEXT NOT NULL,
    status TEXT NOT NULL,
    client_label TEXT,
    started_at TEXT,
    finished_at TEXT
);

CREATE INDEX ix_queue_fifo ON queue_items (status, enqueued_at);

CREATE TABLE play_history (
    id TEXT PRIMARY KEY,
    media_id TEXT NOT NULL,
    zone_id TEXT NOT NULL DEFAULT 'main',
    played_at TEXT NOT NULL,
    origin TEXT NOT NULL,
    completed INTEGER NOT NULL,
    title_snapshot TEXT,
    artist_snapshot TEXT
);

CREATE INDEX ix_history_recent ON play_history (played_at, origin, media_id);

CREATE TABLE settings (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE TABLE admin_sessions (
    id TEXT PRIMARY KEY,
    token_hash TEXT NOT NULL UNIQUE,
    created_at TEXT NOT NULL,
    expires_at TEXT NOT NULL
);

CREATE TABLE backups (
    id TEXT PRIMARY KEY,
    kind TEXT NOT NULL,
    path TEXT NOT NULL,
    size_bytes INTEGER NOT NULL,
    created_at TEXT NOT NULL,
    note TEXT
);

CREATE TABLE audit_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    at TEXT NOT NULL,
    action TEXT NOT NULL,
    detail TEXT
);
