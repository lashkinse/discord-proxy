using System.Net.Mime;
using System.Text.Json;
using DiscordProxy.Data;
using DiscordProxy.Services;
using DiscordProxy.Validation;

namespace DiscordProxy.Endpoints;

/// <summary>
/// Proxy HTTP handlers. A thin layer: validate input, enqueue, answer briefly.
/// All the heavy lifting (sending, retries) lives in <see cref="Services.WebhookWorker"/>.
/// </summary>
public static class WebhookEndpoints
{
    public static void MapWebhookEndpoints(this WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));

        app.MapGet("/health/ready", (QueueStore store) =>
            store.Ping() ? Results.Ok(new { status = "ok" }) : Results.StatusCode(503));

        app.MapGet("/stats", (QueueStore store) =>
            Results.Ok(new { pending = store.PendingCount(), dead = store.DeadCount() }));

        // Webhook check, passed through: the queue is untouched,
        // Discord's answer is returned as-is.
        app.MapGet("/api/webhooks/{id}/{token}", async (
            string id,
            string token,
            HttpContext context,
            IHttpClientFactory http) =>
        {
            if (!WebhookValidator.IsValidId(id) || !WebhookValidator.IsValidToken(token))
                return Results.BadRequest(new { message = "Invalid webhook id/token" });

            var client = http.CreateClient(DiscordSender.HttpClientName);
            var url = $"https://discord.com/api/webhooks/{id}/{token}{context.Request.QueryString}";
            try
            {
                using var response = await client.GetAsync(url, context.RequestAborted);
                var body = await response.Content.ReadAsStringAsync(context.RequestAborted);
                return Results.Content(body, "application/json", statusCode: (int)response.StatusCode);
            }
            catch (Exception ex)
            {
                return Results.Problem($"Upstream error: {ex.Message}", statusCode: 502);
            }
        });

        // Message intake: validate, store in SQLite, answer 202.
        // Delivery happens later, done by the worker.
        app.MapPost("/api/webhooks/{id}/{token}", async (
            string id,
            string token,
            HttpContext context,
            QueueStore store,
            ProxyOptions options) =>
        {
            if (!WebhookValidator.IsValidId(id) || !WebhookValidator.IsValidToken(token))
            {
                app.Logger.LogWarning("Rejected {WebhookId}: invalid id/token", id);
                return Results.BadRequest(new { message = "Invalid webhook id/token" });
            }

            var (failure, payload, reason) = await ValidateIntakeAsync(context, store, options);
            if (failure is not null)
            {
                // Truncated like Discord errors elsewhere: enough to debug, not enough to spam.
                var preview = payload.Length <= 300 ? payload : payload[..300];
                app.Logger.LogWarning("Rejected {WebhookId}: {Reason}. Body: {Body}", id, reason, preview);
                return failure;
            }

            var jobId = store.Enqueue(id, token, context.Request.QueryString.Value ?? string.Empty, payload);
            return Results.Accepted(value: new { jobId, status = "queued" });
        });
    }

    // All intake checks in one flat sequence. A null failure means the payload may be enqueued.
    // The reason mirrors the response body and goes to the log next to the webhook id.
    private static async Task<(IResult? Failure, string Payload, string? Reason)> ValidateIntakeAsync(
        HttpContext context, QueueStore store, ProxyOptions options)
    {
        if (context.Request.ContentType is null ||
            !context.Request.ContentType.StartsWith(MediaTypeNames.Application.Json, StringComparison.OrdinalIgnoreCase))
            return (Results.BadRequest(new { message = "Content-Type must be application/json" }), string.Empty, "bad content type");

        // Reject oversized bodies before reading them into memory.
        if (context.Request.ContentLength > options.MaxPayloadBytes)
            return (Results.StatusCode(413), string.Empty, "body too large");

        string rawBody;
        try
        {
            // Read as text (not ReadFromJsonAsync): the exact bytes go to the queue and to Discord.
            using var reader = new StreamReader(context.Request.Body);
            rawBody = await reader.ReadToEndAsync();
        }
        catch
        {
            return (Results.BadRequest(new { message = "Cannot read body" }), string.Empty, "unreadable body");
        }

        if (rawBody.Length > options.MaxPayloadBytes)
            return (Results.StatusCode(413), rawBody, "body too large");

        string? payloadError;
        try
        {
            using var document = JsonDocument.Parse(rawBody);
            payloadError = WebhookValidator.ValidatePayload(document.RootElement);
        }
        catch (JsonException)
        {
            return (Results.BadRequest(new { message = "Invalid JSON" }), rawBody, "invalid JSON");
        }
        if (payloadError is not null)
            return (Results.BadRequest(new { message = payloadError }), rawBody, payloadError);

        if (store.PendingCount() > options.MaxPending)
            return (Results.Json(new { message = "Queue overloaded" }, statusCode: 503), string.Empty, "queue overloaded");

        return (null, rawBody, null);
    }
}
