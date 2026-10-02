using DiscordProxy.Data;
using DiscordProxy.Models;

namespace DiscordProxy.Services;

/// <summary>
/// Background worker: takes due jobs and sends them via <see cref="IDiscordSender"/>.
/// Each webhook gets its own lane: different webhooks send concurrently,
/// but a webhook never has two sends in flight, so per-webhook order holds.
/// Outcome decisions live here, HTTP details in the sender, SQL in the store.
/// Pacing memory is volatile: after a restart the first batch goes out at once,
/// and the 429 path absorbs the burst.
/// </summary>
public sealed class WebhookWorker : BackgroundService
{
    private readonly QueueStore _store;
    private readonly IDiscordSender _sender;
    private readonly DeliveryPacer _pacer;
    private readonly ProxyOptions _options;
    private readonly ILogger<WebhookWorker> _log;

    // Guards _inFlight, _pacer and _globalPauseUntil across concurrent send tasks.
    private readonly object _sync = new();
    private readonly HashSet<string> _inFlight = new();

    private const int BatchSize = 20;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PausePollDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(1);

    // A global 429 (global:true) pauses ALL sends until the given time.
    private DateTime _globalPauseUntil = DateTime.MinValue;

    public WebhookWorker(
        QueueStore store,
        IDiscordSender sender,
        ProxyOptions options,
        ILogger<WebhookWorker> log)
    {
        _store = store;
        _sender = sender;
        _options = options;
        _pacer = new DeliveryPacer(options.PacePerWebhook);
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var running = new List<Task>();
            try
            {
                if (IsPaused())
                {
                    await Task.Delay(PausePollDelay, stoppingToken);
                    continue;
                }

                var dueJobs = _store.GetDue(BatchSize);
                if (dueJobs.Count == 0)
                {
                    await Task.Delay(IdleDelay, stoppingToken);
                    continue;
                }

                foreach (var job in dueJobs)
                {
                    if (stoppingToken.IsCancellationRequested || IsPaused())
                        break;

                    var launch = false;
                    DateTime waitUntil = default;
                    var waiting = false;
                    lock (_sync)
                    {
                        // The head may still be in flight from an earlier batch:
                        // SQL only knows due times, the lane set knows the truth.
                        if (_inFlight.Contains(job.WebhookId))
                            continue;
                        if (_pacer.ShouldWait(job.WebhookId, DateTime.UtcNow, out var notBefore))
                        {
                            waiting = true;
                            waitUntil = notBefore;
                        }
                        else
                        {
                            _inFlight.Add(job.WebhookId);
                            launch = true;
                        }
                    }

                    if (waiting)
                    {
                        // Pacing is not a send attempt: it must not burn retries.
                        _store.Postpone(job.Id, waitUntil, error: null, countAttempt: false);
                        continue;
                    }
                    if (launch)
                        running.Add(RunOneAsync(job, stoppingToken));
                }

                // Apply outcomes as they arrive: a hanging webhook holds only its lane.
                while (running.Count > 0)
                {
                    var done = await Task.WhenAny(running);
                    running.Remove(done);
                    await done; // RunOneAsync never throws (see below).
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Worker loop error");
                try
                {
                    await Task.Delay(ErrorDelay, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private bool IsPaused()
    {
        lock (_sync)
        {
            return DateTime.UtcNow < _globalPauseUntil;
        }
    }

    private async Task RunOneAsync(QueuedJob job, CancellationToken stoppingToken)
    {
        try
        {
            SendOutcome outcome;
            try
            {
                outcome = await _sender.SendAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return; // Shutting down — the job stays queued.
            }
            // Every completed attempt paces the webhook, even a failed one:
            // otherwise an all-failing stream would hammer Discord with no gaps.
            lock (_sync)
            {
                _pacer.MarkSent(job.WebhookId, DateTime.UtcNow);
            }
            ApplyOutcome(job, outcome);
        }
        catch (Exception ex)
        {
            // Store failures and the like: logged, the job stays queued for the next pass.
            _log.LogError(ex, "Send task error for job {JobId}", job.Id);
        }
        finally
        {
            lock (_sync)
            {
                _inFlight.Remove(job.WebhookId);
            }
        }
    }

    private void ApplyOutcome(QueuedJob job, SendOutcome outcome)
    {
        switch (outcome)
        {
            case Delivered delivered:
                _store.Delete(job.Id);
                _log.LogDebug("Delivered job {JobId} ({WebhookId}) -> {Status}", job.Id, job.WebhookId, delivered.StatusCode);
                break;

            case RateLimited limited:
                var resumeAt = DateTime.UtcNow + TimeSpan.FromSeconds(limited.RetryAfterSeconds + 0.5);
                lock (_sync)
                {
                    // Set before the dead check: a dying job must still shield the rest.
                    if (limited.IsGlobal)
                        _globalPauseUntil = resumeAt;
                }
                if (job.Attempts + 1 > _options.MaxAttempts429)
                    MoveToDead(job, $"429 attempts exceeded (last retry_after={limited.RetryAfterSeconds})");
                else
                {
                    _store.Postpone(job.Id, resumeAt, $"429 retry_after={limited.RetryAfterSeconds}");
                    _log.LogWarning("429 for job {JobId} ({WebhookId}), waiting {Wait}s", job.Id, job.WebhookId, limited.RetryAfterSeconds);
                }
                break;

            case Retryable retryable:
                if (job.Attempts + 1 > _options.MaxAttemptsNet)
                {
                    MoveToDead(job, retryable.Error);
                }
                else
                {
                    // Capped at 15 minutes.
                    var waitSeconds = Math.Min(5 * Math.Pow(2, job.Attempts), 900);
                    var retryAt = DateTime.UtcNow + TimeSpan.FromSeconds(waitSeconds);
                    _store.Postpone(job.Id, retryAt, retryable.Error);
                    _log.LogWarning("Retry job {JobId} ({WebhookId}) in {Wait}s: {Error}", job.Id, job.WebhookId, waitSeconds, retryable.Error);
                }
                break;

            case Permanent permanent:
                MoveToDead(job, permanent.Error);
                break;
        }
    }

    private void MoveToDead(QueuedJob job, string error)
    {
        _store.MarkDead(job.Id, error);
        _log.LogWarning("Dead job {JobId} (webhook {WebhookId}): {Error}", job.Id, job.WebhookId, error);
    }
}
