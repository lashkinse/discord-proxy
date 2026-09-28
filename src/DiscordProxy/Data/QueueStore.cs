using Microsoft.Data.Sqlite;
using DiscordProxy.Models;

namespace DiscordProxy.Data;

/// <summary>
/// SQLite-backed queue. Each method opens one connection per operation,
/// which keeps concurrent access from the API and the worker simple.
/// WAL mode plus busy_timeout protect against SQLITE_BUSY;
/// synchronous FULL keeps committed jobs safe across power loss.
/// </summary>
public sealed class QueueStore
{
    private readonly string _connectionString;

    public QueueStore(string dbPath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
        Initialize();
    }

    public QueueStore(ProxyOptions options) : this(options.GetDbPath()) { }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS queue(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              webhook_id TEXT NOT NULL,
              token TEXT NOT NULL,
              query TEXT NOT NULL DEFAULT '',
              payload TEXT NOT NULL,
              attempts INTEGER NOT NULL DEFAULT 0,
              next_attempt_at TEXT NOT NULL,
              created_at TEXT NOT NULL,
              last_error TEXT
            );
            CREATE TABLE IF NOT EXISTS dead(
              id INTEGER PRIMARY KEY,
              webhook_id TEXT NOT NULL,
              token TEXT NOT NULL,
              query TEXT NOT NULL DEFAULT '',
              payload TEXT NOT NULL,
              attempts INTEGER NOT NULL DEFAULT 0,
              created_at TEXT NOT NULL,
              failed_at TEXT NOT NULL,
              last_error TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_queue_due ON queue(next_attempt_at, id);
            CREATE INDEX IF NOT EXISTS idx_queue_webhook ON queue(webhook_id, id);
            DELETE FROM dead WHERE failed_at < datetime('now', '-7 days');
            """;
        command.ExecuteNonQuery();
    }

    private static string ToTimestamp(DateTime value) => value.ToUniversalTime().ToString("o");

    /// <summary>Database liveness check for /health/ready.</summary>
    public bool Ping()
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            command.ExecuteScalar();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Enqueues a job and returns its id.
    /// The INSERT is synchronous: once we answered 202, the job is on disk.
    /// </summary>
    public long Enqueue(string webhookId, string token, string query, string payload)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO queue(webhook_id, token, query, payload, next_attempt_at, created_at)
            VALUES($webhook, $token, $query, $payload, $next, $created);
            SELECT last_insert_rowid();
            """;
        var now = ToTimestamp(DateTime.UtcNow);
        command.Parameters.AddWithValue("$webhook", webhookId);
        command.Parameters.AddWithValue("$token", token);
        command.Parameters.AddWithValue("$query", query);
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$next", now);
        command.Parameters.AddWithValue("$created", now);
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    /// <summary>
    /// Jobs due for sending. Only the head (smallest id) of each webhook is
    /// returned, so a waiting head blocks its webhook: message order survives
    /// pacing and 429 retries. Ordered by id across webhooks.
    /// </summary>
    public List<QueuedJob> GetDue(int limit)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, webhook_id, token, query, payload, attempts FROM queue q
            WHERE next_attempt_at <= $now
              AND id = (SELECT MIN(id) FROM queue WHERE webhook_id = q.webhook_id)
            ORDER BY id LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$now", ToTimestamp(DateTime.UtcNow));
        command.Parameters.AddWithValue("$limit", limit);

        var jobs = new List<QueuedJob>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            jobs.Add(new QueuedJob(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5)));
        }
        return jobs;
    }

    public long PendingCount() => Scalar("SELECT COUNT(*) FROM queue;");
    public long DeadCount() => Scalar("SELECT COUNT(*) FROM dead;");

    private long Scalar(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public void Delete(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM queue WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>Defer a retry: new due time, error text. Counts as an attempt unless told otherwise.</summary>
    public void Postpone(long id, DateTime nextAttemptAt, string? error, bool countAttempt = true)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE queue SET next_attempt_at = $next, attempts = attempts + $inc, last_error = $error
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$next", ToTimestamp(nextAttemptAt));
        command.Parameters.AddWithValue("$inc", countAttempt ? 1 : 0);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>Hopeless job: move it to dead for auditing.</summary>
    public void MarkDead(long id, string error)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO dead(id, webhook_id, token, query, payload, attempts, created_at, failed_at, last_error)
            SELECT id, webhook_id, token, query, payload, attempts + 1, created_at, $failed, $error
            FROM queue WHERE id = $id;
            """;
        insert.Parameters.AddWithValue("$failed", ToTimestamp(DateTime.UtcNow));
        insert.Parameters.AddWithValue("$error", error);
        insert.Parameters.AddWithValue("$id", id);
        insert.ExecuteNonQuery();
        ExecuteIn(connection, "DELETE FROM queue WHERE id = $id;", id);
        transaction.Commit();
    }

    private void ExecuteIn(SqliteConnection connection, string sql, long id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }
}
