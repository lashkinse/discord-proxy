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
    /// Fields present with a wrong type are rejected here instead of dying
    /// later as Discord 4xx (e.g. "embeds": "0" from a buggy plugin).
    /// Deep validation is left to Discord. Returns an error, or null when valid.
    /// </summary>
    public static string? ValidatePayload(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return "Body must be a JSON object";

        if (root.TryGetProperty("content", out var content))
        {
            if (content.ValueKind != JsonValueKind.String)
                return "Content must be a string";
            if (content.GetString()!.Length > 2000)
                return "Content must be 2000 or fewer in length";
        }

        if (root.TryGetProperty("embeds", out var embeds))
        {
            if (embeds.ValueKind != JsonValueKind.Array)
                return "Embeds must be an array";
            if (embeds.GetArrayLength() > 10)
                return "At most 10 embeds allowed";
        }

        if (root.TryGetProperty("components", out var components) && components.ValueKind != JsonValueKind.Array)
            return "Components must be an array";

        if (root.TryGetProperty("poll", out var poll) && poll.ValueKind != JsonValueKind.Object)
            return "Poll must be an object";

        // A missing property leaves poll as default(Undefined), so this also means "no poll".
        if (HasNonEmptyString(root, "content")
            || HasNonEmptyArray(root, "embeds")
            || HasNonEmptyArray(root, "components")
            || poll.ValueKind == JsonValueKind.Object)
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
