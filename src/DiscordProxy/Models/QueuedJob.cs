namespace DiscordProxy.Models;

/// <summary>
/// One send job: raw JSON plus the Discord webhook address.
/// The payload is stored as-is and forwarded unchanged.
/// </summary>
public sealed record QueuedJob(
    long Id,
    string WebhookId,
    string Token,
    string Query,
    string Payload,
    int Attempts);
