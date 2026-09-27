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
            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync(url, context.RequestAborted);
            }
            catch (Exception ex)
            {
                return Results.Problem("Upstream error: " + ex.Message, statusCode: 502);
            }

            var body = await response.Content.ReadAsStringAsync(context.RequestAborted);
            return Results.Content(body, "application/json", statusCode: (int)response.StatusCode);
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
                return Results.BadRequest(new { message = "Invalid webhook id/token" });

            if (context.Request.ContentType is null ||
                !context.Request.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { message = "Content-Type must be application/json" });

            string rawBody;
            try
            {
                // Read as text (not ReadFromJsonAsync): the exact bytes go to the queue and to Discord.
                using var reader = new StreamReader(context.Request.Body);
                rawBody = await reader.ReadToEndAsync();
            }
            catch
            {
                return Results.BadRequest(new { message = "Cannot read body" });
            }

            if (rawBody.Length > options.MaxPayloadBytes)
                return Results.StatusCode(413);

            string? payloadError;
            try
            {
                using var document = JsonDocument.Parse(rawBody);
                payloadError = WebhookValidator.ValidatePayload(document.RootElement);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { message = "Invalid JSON" });
            }
            if (payloadError is not null)
                return Results.BadRequest(new { message = payloadError });

            if (store.PendingCount() > options.MaxPending)
                return Results.Json(new { message = "Queue overloaded" }, statusCode: 503);

            var jobId = store.Enqueue(id, token, context.Request.QueryString.Value ?? string.Empty, rawBody);
            return Results.Accepted(value: new { jobId, status = "queued" });
        });
    }
}
