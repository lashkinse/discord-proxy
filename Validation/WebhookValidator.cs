using System.Text.Json;
using System.Text.RegularExpressions;

namespace DiscordProxy.Validation;

/// <summary>
/// Pure validation functions. No I/O, easy to unit test.
/// </summary>
public static class WebhookValidator
{
    private static readonly Regex IdPattern = new(@"^\d{17,20}$", RegexOptions.Compiled);
    private static readonly Regex TokenPattern = new(@"^[A-Za-z0-9._\-]{20,200}$", RegexOptions.Compiled);

    /// <summary>Discord webhook id is a snowflake: 17-20 digits.</summary>
    public static bool IsValidId(string id) => IdPattern.IsMatch(id);

    /// <summary>Webhook token: 20-200 chars from a safe alphabet.</summary>
    public static bool IsValidToken(string token) => TokenPattern.IsMatch(token);

    /// <summary>
    /// Minimal body check: an object with at least one message field.
    /// Deep validation is left to Discord — it returns 4xx and the job goes
    /// dead without retries. Returns an error message, or null when valid.
    /// </summary>
    public static string? ValidatePayload(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return "Body must be a JSON object";

        if (HasNonEmptyString(root, "content")
            || HasNonEmptyArray(root, "embeds")
            || HasNonEmptyArray(root, "components")
            || (root.TryGetProperty("poll", out var poll) && poll.ValueKind == JsonValueKind.Object))
            return null;

        return "Cannot send an empty message";
    }

    private static bool HasNonEmptyString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString()!.Length > 0;

    private static bool HasNonEmptyArray(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Array
        && value.GetArrayLength() > 0;
}
