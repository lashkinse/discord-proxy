namespace DiscordProxy;

/// <summary>
/// Proxy settings. Sources, weakest to strongest:
/// appsettings.json ("Proxy" section), Proxy__* environment variables,
/// command-line arguments (--Proxy:Port=7071),
/// legacy PORT / DB_PATH variables (applied last, kept for backward compatibility).
/// </summary>
public sealed class ProxyOptions
{
    public const string SectionName = "Proxy";

    /// <summary>HTTP server port.</summary>
    public int Port { get; set; } = 7070;

    /// <summary>
    /// Path to the SQLite queue file. Relative paths resolve
    /// against the application directory.
    /// </summary>
    public string DbPath { get; set; } = "queue.db";

    /// <summary>Maximum request body size in bytes.</summary>
    public int MaxPayloadBytes { get; set; } = 64 * 1024;

    /// <summary>Maximum pending jobs; beyond this we answer 503.</summary>
    public int MaxPending { get; set; } = 10_000;

    /// <summary>Minimum gap between sends to one webhook, in seconds (Discord limit is ~30/min).</summary>
    public double PaceSeconds { get; set; } = 2.2;

    /// <summary>Attempts on network errors and 5xx before a job goes dead.</summary>
    public int MaxAttemptsNet { get; set; } = 12;

    /// <summary>Attempts on 429 before a job goes dead.</summary>
    public int MaxAttempts429 { get; set; } = 25;

    /// <summary>Timeout of a single request to Discord, in seconds.</summary>
    public double DiscordTimeoutSeconds { get; set; } = 15;

    public TimeSpan PacePerWebhook => TimeSpan.FromSeconds(PaceSeconds);
    public TimeSpan DiscordTimeout => TimeSpan.FromSeconds(DiscordTimeoutSeconds);

    /// <summary>Absolute database path, resolving relatives against the app directory.</summary>
    public string GetDbPath() =>
        Path.IsPathRooted(DbPath) ? DbPath : Path.Combine(AppContext.BaseDirectory, DbPath);
}
