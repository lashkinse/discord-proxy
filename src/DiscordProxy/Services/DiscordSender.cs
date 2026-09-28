using System.Net.Mime;
using System.Text;
using System.Text.Json;
using DiscordProxy.Models;

namespace DiscordProxy.Services;

/// <summary>Result of one send attempt. The caller decides what to do next.</summary>
public abstract record SendOutcome;

public sealed record Delivered(int StatusCode) : SendOutcome;

/// <summary>Discord returned 429: wait exactly retry_after.</summary>
public sealed record RateLimited(double RetryAfterSeconds, bool IsGlobal) : SendOutcome;

/// <summary>Transient error (network, timeout, 5xx): retry with backoff.</summary>
public sealed record Retryable(string Error) : SendOutcome;

/// <summary>Permanent error (other 4xx): retries are pointless.</summary>
public sealed record Permanent(string Error) : SendOutcome;

/// <summary>
/// Sends a single job to Discord. No queues, no sleeps — just HTTP and response parsing.
/// </summary>
public sealed class DiscordSender : IDiscordSender
{
    public const string HttpClientName = "discord";

    private readonly IHttpClientFactory _httpClientFactory;

    public DiscordSender(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<SendOutcome> SendAsync(QueuedJob job, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var url = $"https://discord.com/api/webhooks/{job.WebhookId}/{job.Token}{job.Query}";
        using var content = new StringContent(job.Payload, Encoding.UTF8, MediaTypeNames.Application.Json);

        HttpResponseMessage response;
        string body;
        try
        {
            response = await client.PostAsync(url, content, cancellationToken);
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch
            {
                body = string.Empty;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Shutting down — not a send error.
        }
        catch (Exception ex)
        {
            return new Retryable(ex.Message);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (status is >= 200 and < 300)
                return new Delivered(status);

            if (status == 429)
                return new RateLimited(ParseRetryAfter(body), IsGlobal(body));

            if (status >= 500)
                return new Retryable($"HTTP {status}: {Truncate(body)}");

            return new Permanent($"HTTP {status}: {Truncate(body)}");
        }
    }

    /// <summary>
    /// Discord reports seconds as a fraction in the {"retry_after": ...} body.
    /// The Retry-After header is not trusted: it has been seen in wrong units.
    /// </summary>
    internal static double ParseRetryAfter(string body, double fallbackSeconds = 5.0)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("retry_after", out var element)
                && element.TryGetDouble(out var seconds)
                && seconds is >= 0 and < 3600)
                return seconds;
        }
        catch { }
        return fallbackSeconds;
    }

    internal static bool IsGlobal(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("global", out var element)
                && element.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private static string Truncate(string value, int maxLength = 300) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
