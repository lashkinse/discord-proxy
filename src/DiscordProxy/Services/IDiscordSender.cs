using DiscordProxy.Models;

namespace DiscordProxy.Services;

/// <summary>
/// Sends one queued job to Discord. Separated for testability:
/// tests substitute a stub, production uses <see cref="DiscordSender"/>.
/// </summary>
public interface IDiscordSender
{
    Task<SendOutcome> SendAsync(QueuedJob job, CancellationToken cancellationToken);
}
