using System.Net;
using System.Net.Sockets;

namespace DiscordProxy.Services;

/// <summary>
/// Fast failover across resolved IPs (poor man's Happy Eyeballs).
/// Background: individual Cloudflare edges for discord.com occasionally
/// blackhole traffic (observed 2026-09-28: 162.159.136.232 timed out on TCP
/// while siblings answered in ~50ms). The default connector spends the whole
/// HttpClient timeout on the first dead address; here each address gets
/// a short budget and we move on to the next one.
/// </summary>
public static class DiscordConnect
{
    private static readonly TimeSpan PerAddressTimeout = TimeSpan.FromSeconds(2);

    public static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        Exception? lastError = null;

        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptTimeout.CancelAfter(PerAddressTimeout);
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), attemptTimeout.Token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                throw; // Shutting down — not a dead address.
            }
            catch (Exception ex)
            {
                socket.Dispose();
                lastError = ex; // Dead address (or our 2s budget): try the next one.
            }
        }

        throw lastError ?? new HttpRequestException($"No reachable address for {context.DnsEndPoint.Host}.");
    }
}
