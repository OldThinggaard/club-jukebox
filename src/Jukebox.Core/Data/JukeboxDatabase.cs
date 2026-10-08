using Microsoft.Data.Sqlite;

namespace Jukebox.Core.Data;

public sealed class JukeboxDatabase
{
    private readonly SemaphoreSlim _write = new(1, 1);

    public JukeboxDatabase(JukeboxPaths paths)
    {
        Paths = paths;
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public JukeboxPaths Paths { get; }
    public string ConnectionString { get; }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public void Migrate()
    {
        using var connection = Open();
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL UNIQUE,
                applied_at TEXT NOT NULL
            );
            """);
        using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteScalar();
        }

        Execute(connection, "PRAGMA synchronous=NORMAL;");
        var assembly = typeof(JukeboxDatabase).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal);

        foreach (var resource in resources)
        {
            var name = resource.Split('.').Reverse().Skip(1).First();
            if (resource.Contains("001_init", StringComparison.Ordinal))
                name = "001_init";

            if (Applied(connection, name))
                continue;

            using var stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException($"Missing {resource}");
            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();
            using var tx = connection.BeginTransaction();
            Execute(connection, sql, tx);
            Execute(connection, "INSERT INTO schema_migrations(name, applied_at) VALUES ($name, $at);", tx,
                ("$name", name), ("$at", ClubCulture.Iso(DateTime.UtcNow)));
            tx.Commit();
        }
    }

    public string IntegrityCheck()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        return command.ExecuteScalar()?.ToString() ?? "fail";
    }

    public T InWrite<T>(Func<SqliteConnection, SqliteTransaction, T> work)
    {
        _write.Wait();
        try
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            var result = work(connection, tx);
            tx.Commit();
            return result;
        }
        finally
        {
            _write.Release();
        }
    }

    public void InWrite(Action<SqliteConnection, SqliteTransaction> work) =>
        InWrite<object?>((connection, tx) =>
        {
            work(connection, tx);
            return null;
        });

    public static void Execute(SqliteConnection connection, string sql, SqliteTransaction? tx = null, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public static SqliteDataReader Query(SqliteConnection connection, string sql, SqliteTransaction? tx = null, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command.ExecuteReader();
    }

    private static bool Applied(SqliteConnection connection, string name)
    {
        using var reader = Query(connection, "SELECT 1 FROM schema_migrations WHERE name = $name LIMIT 1;", null, ("$name", name));
        return reader.Read();
    }
}
