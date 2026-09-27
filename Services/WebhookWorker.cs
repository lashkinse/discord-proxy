using DiscordProxy.Data;
using DiscordProxy.Models;

namespace DiscordProxy.Services;

/// <summary>
/// Background worker: takes due jobs and sends them via <see cref="DiscordSender"/>.
/// Outcome decisions live here, HTTP details in the sender, SQL in the store.
/// Pacing memory is volatile: after a restart the first batch goes out at once,
/// and the 429 path absorbs the burst.
/// </summary>
public sealed class WebhookWorker : BackgroundService
{
    private readonly QueueStore _store;
    private readonly DiscordSender _sender;
    private readonly DeliveryPacer _pacer;
    private readonly ProxyOptions _options;
    private readonly ILogger<WebhookWorker> _log;

    private const int BatchSize = 20;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PausePollDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(1);

    // A global 429 (global:true) pauses ALL sends until the given time.
    private DateTime _globalPauseUntil = DateTime.MinValue;

    public WebhookWorker(
        QueueStore store,
        DiscordSender sender,
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
            try
            {
                if (DateTime.UtcNow < _globalPauseUntil)
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
                    if (stoppingToken.IsCancellationRequested)
                        break;
                    if (DateTime.UtcNow < _globalPauseUntil)
                        break;

                    if (_pacer.ShouldWait(job.WebhookId, DateTime.UtcNow, out var notBefore))
                    {
                        // Pacing is not a send attempt: it must not burn retries.
                        _store.Postpone(job.Id, notBefore, error: null, countAttempt: false);
                        continue;
                    }

                    await ProcessAsync(job, stoppingToken);
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

    private async Task ProcessAsync(QueuedJob job, CancellationToken stoppingToken)
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

        switch (outcome)
        {
            case Delivered delivered:
                _pacer.MarkSent(job.WebhookId, DateTime.UtcNow);
                _store.Delete(job.Id);
                _log.LogInformation("Delivered job {JobId} -> {Status}", job.Id, delivered.StatusCode);
                break;

            case RateLimited limited:
                var resumeAt = DateTime.UtcNow + TimeSpan.FromSeconds(limited.RetryAfterSeconds + 0.5);
                if (limited.IsGlobal)
                    _globalPauseUntil = resumeAt;
                if (job.Attempts + 1 > _options.MaxAttempts429)
                    Bury(job, $"429 attempts exceeded (last retry_after={limited.RetryAfterSeconds})");
                else
                {
                    _store.Postpone(job.Id, resumeAt, $"429 retry_after={limited.RetryAfterSeconds}");
                    _log.LogWarning("429 for job {JobId}, waiting {Wait}s", job.Id, limited.RetryAfterSeconds);
                }
                break;

            case Retryable retryable:
                if (job.Attempts + 1 > _options.MaxAttemptsNet)
                {
                    Bury(job, retryable.Error);
                }
                else
                {
                    // Capped at 15 minutes.
                    var waitSeconds = Math.Min(5 * Math.Pow(2, job.Attempts), 900);
                    var retryAt = DateTime.UtcNow + TimeSpan.FromSeconds(waitSeconds);
                    _store.Postpone(job.Id, retryAt, retryable.Error);
                    _log.LogWarning("Retry job {JobId} in {Wait}s: {Error}", job.Id, waitSeconds, retryable.Error);
                }
                break;

            case Permanent permanent:
                Bury(job, permanent.Error);
                break;
        }
    }

    private void Bury(QueuedJob job, string error)
    {
        _store.MarkDead(job.Id, error);
        _log.LogWarning("Dead job {JobId}: {Error}", job.Id, error);
    }
}
