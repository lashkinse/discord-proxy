namespace DiscordProxy.Services;

/// <summary>
/// Enforces the minimum interval between sends to one address.
/// A single worker plus ORDER BY id in the queue keeps message order.
/// </summary>
public sealed class DeliveryPacer
{
    private readonly TimeSpan _minInterval;
    private readonly Dictionary<string, DateTime> _lastSentByKey = new();

    public DeliveryPacer(TimeSpan minInterval)
    {
        _minInterval = minInterval;
    }

    /// <summary>
    /// True means it is too early to send; try again no earlier than the returned time.
    /// </summary>
    public bool ShouldWait(string key, DateTime now, out DateTime notBefore)
    {
        notBefore = now;
        if (!_lastSentByKey.TryGetValue(key, out var last))
            return false;

        notBefore = last + _minInterval;
        return now < notBefore;
    }

    public void MarkSent(string key, DateTime now) => _lastSentByKey[key] = now;
}
