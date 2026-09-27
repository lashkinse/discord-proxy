using System.Collections.Concurrent;
using DiscordProxy.Data;
using DiscordProxy.Models;
using DiscordProxy.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordProxy.Tests;

/// <summary>
/// Worker decisions with a stubbed sender and a real temp SQLite store:
/// no network, deterministic.
/// </summary>
public sealed class WorkerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly QueueStore _store;
    private readonly StubSender _sender = new();
    private readonly ProxyOptions _options = new();
    private readonly List<WebhookWorker> _workers = new();

    public WorkerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"proxy-worker-{Guid.NewGuid():N}.db");
        _store = new QueueStore(_dbPath);
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            try { File.Delete(_dbPath + suffix); } catch { }
        }
    }

    private sealed class StubSender : IDiscordSender
    {
        public readonly ConcurrentQueue<SendOutcome> Outcomes = new();
        public int Calls;
        public Task<SendOutcome> SendAsync(QueuedJob job, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(Outcomes.TryDequeue(out var outcome) ? outcome : new Delivered(204));
        }
    }

    private async Task RunWorkerUntilAsync(Func<bool> done, int timeoutMs = 10000)
    {
        var worker = new WebhookWorker(_store, _sender, _options, NullLogger<WebhookWorker>.Instance);
        _workers.Add(worker);
        var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);
        try
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (done())
                    return;
                await Task.Delay(100, cts.Token);
            }
            Assert.Fail("Condition was not met in time.");
        }
        finally
        {
            cts.Cancel();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private long Enqueue() =>
        _store.Enqueue("111111111111111111", "token", "", @"{""content"":""x""}");

    [Fact]
    public async Task DeliveredJobLeavesQueue()
    {
        _sender.Outcomes.Enqueue(new Delivered(204));
        Enqueue();
        await RunWorkerUntilAsync(() => _store.PendingCount() == 0);
        Assert.Equal(0, _store.DeadCount());
        Assert.Equal(1, _sender.Calls);
    }

    [Fact]
    public async Task RateLimitedJobRetriesAndDelivers()
    {
        _sender.Outcomes.Enqueue(new RateLimited(0.5, IsGlobal: false));
        Enqueue();
        await RunWorkerUntilAsync(() => _store.PendingCount() == 0, timeoutMs: 15000);
        Assert.Equal(0, _store.DeadCount());
        Assert.Equal(2, _sender.Calls); // first 429, then success
    }

    [Fact]
    public async Task RetryableUsesBackoffThenDelivers()
    {
        _sender.Outcomes.Enqueue(new Retryable("boom"));
        Enqueue();
        await RunWorkerUntilAsync(() => _store.PendingCount() == 0, timeoutMs: 20000);
        Assert.Equal(0, _store.DeadCount());
        Assert.Equal(2, _sender.Calls);
    }

    [Fact]
    public async Task ExhaustedRetriesBuryTheJob()
    {
        var id = Enqueue();
        for (var i = 0; i < _options.MaxAttemptsNet; i++)
            _store.Postpone(id, DateTime.UtcNow.AddSeconds(-1), "boom");
        _sender.Outcomes.Enqueue(new Retryable("boom"));
        await RunWorkerUntilAsync(() => _store.DeadCount() == 1);
        Assert.Equal(0, _store.PendingCount());
    }

    [Fact]
    public async Task PermanentErrorBuriesImmediately()
    {
        _sender.Outcomes.Enqueue(new Permanent("HTTP 401"));
        Enqueue();
        await RunWorkerUntilAsync(() => _store.DeadCount() == 1);
        Assert.Equal(0, _store.PendingCount());
        Assert.Equal(1, _sender.Calls); // no retry
    }

    [Fact]
    public async Task LaterJobsWaitForBlockedHead()
    {
        // Head gets a long 429; the second job must not jump ahead of it.
        _sender.Outcomes.Enqueue(new RateLimited(5, IsGlobal: false));
        Enqueue();
        Enqueue();
        await RunWorkerUntilAsync(() => _sender.Calls >= 1, timeoutMs: 5000);

        // Only the head was attempted; the second job is still queued behind it.
        Assert.Equal(1, _sender.Calls);
        Assert.Equal(2, _store.PendingCount());
    }
}
