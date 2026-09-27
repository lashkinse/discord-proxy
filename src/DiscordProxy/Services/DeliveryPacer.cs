namespace DiscordProxy.Services;

/// <summary>
/// Enforces the minimum interval between sends to one address.
/// Combined with head-of-line selection in the queue, message order is kept.
/// Single-threaded use by the worker only; not thread-safe.
/// </summary>
public sealed class DeliveryPacer
{
    private static readonly TimeSpan EntryLifetime = TimeSpan.FromHours(1);

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

    public void MarkSent(string key, DateTime now)
    {
        _lastSentByKey[key] = now;
        if (_lastSentByKey.Count % 128 == 0)
            Trim(now);
    }

    // One entry per webhook ever seen: drop the ones idle for over an hour.
    private void Trim(DateTime now)
    {
        List<string>? stale = null;
        foreach (var (key, sentAt) in _lastSentByKey)
        {
            if (now - sentAt > EntryLifetime)
                (stale ??= []).Add(key);
        }
        if (stale is not null)
        {
            foreach (var key in stale)
                _lastSentByKey.Remove(key);
        }
    }
}
