namespace DiscordProxy;

/// <summary>
/// Proxy settings. Read from environment variables once at startup.
/// </summary>
public sealed class ProxyOptions
{
    private const int DefaultPort = 7070;

    /// <summary>HTTP server port.</summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>Path to the SQLite queue file.</summary>
    public string DbPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "queue.db");

    /// <summary>Maximum request body size in bytes.</summary>
    public int MaxPayloadBytes { get; init; } = 64 * 1024;

    /// <summary>Maximum pending jobs; beyond this we answer 503.</summary>
    public int MaxPending { get; init; } = 10_000;

    /// <summary>Minimum interval between sends to one webhook (Discord limit is ~30/min).</summary>
    public TimeSpan PacePerWebhook { get; init; } = TimeSpan.FromSeconds(2.2);

    /// <summary>Attempts on network errors and 5xx before a job goes dead.</summary>
    public int MaxAttemptsNet { get; init; } = 12;

    /// <summary>Attempts on 429 before a job goes dead.</summary>
    public int MaxAttempts429 { get; init; } = 25;

    /// <summary>Timeout of a single request to Discord.</summary>
    public TimeSpan DiscordTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Builds settings from the environment: PORT, DB_PATH.</summary>
    public static ProxyOptions FromEnvironment()
    {
        var port = DefaultPort;
        if (int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var p) && p is > 0 and <= 65535)
            port = p;

        var dbPath = Environment.GetEnvironmentVariable("DB_PATH");
        if (string.IsNullOrWhiteSpace(dbPath))
            dbPath = Path.Combine(AppContext.BaseDirectory, "queue.db");

        return new ProxyOptions { Port = port, DbPath = dbPath };
    }
}
