using DiscordProxy.Data;

namespace DiscordProxy.Tests;

/// <summary>Queue behavior on a throwaway SQLite file. Covers the ordering guarantee.</summary>
public sealed class QueueStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly QueueStore _store;

    public QueueStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"proxy-test-{Guid.NewGuid():N}.db");
        _store = new QueueStore(_dbPath);
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            try { File.Delete(_dbPath + suffix); } catch { }
        }
    }

    private static long Enqueue(QueueStore store, string webhook = "111111111111111111") =>
        store.Enqueue(webhook, "token", "", @"{""content"":""x""}");

    [Fact]
    public void EnqueueReturnsIncreasingIds()
    {
        var first = Enqueue(_store);
        var second = Enqueue(_store);
        Assert.True(second > first);
        Assert.Equal(2, _store.PendingCount());
    }

    [Fact]
    public void DueJobsComeInIdOrder()
    {
        Enqueue(_store);
        Enqueue(_store);
        Enqueue(_store);
        var ids = _store.GetDue(10).Select(j => j.Id).ToList();
        Assert.Equal(ids.OrderBy(x => x), ids);
    }

    [Fact]
    public void PostponedHeadBlocksItsWebhook()
    {
        Enqueue(_store, "111111111111111111");
        Enqueue(_store, "111111111111111111");
        Enqueue(_store, "222222222222222222");

        // Only the head of webhook ...111 is postponed: the whole webhook must go silent,
        // while the other webhook still flows.
        var head = _store.GetDue(10).First(j => j.WebhookId == "111111111111111111");
        _store.Postpone(head.Id, DateTime.UtcNow.AddHours(1), null);

        var due = _store.GetDue(10);
        Assert.DoesNotContain(due, j => j.WebhookId == "111111111111111111");
        Assert.Contains(due, j => j.WebhookId == "222222222222222222");
    }

    [Fact]
    public void PacingPostponeDoesNotBurnAttempts()
    {
        var id = Enqueue(_store);
        _store.Postpone(id, DateTime.UtcNow.AddSeconds(-1), null, countAttempt: false);
        var job = Assert.Single(_store.GetDue(10));
        Assert.Equal(id, job.Id);
        Assert.Equal(0, job.Attempts);
    }

    [Fact]
    public void RetryPostponeBurnsOneAttempt()
    {
        var id = Enqueue(_store);
        _store.Postpone(id, DateTime.UtcNow.AddSeconds(-1), "boom");
        var job = Assert.Single(_store.GetDue(10));
        Assert.Equal(id, job.Id);
        Assert.Equal(1, job.Attempts);
        Assert.Equal(1, _store.PendingCount());
        Assert.Equal(0, _store.DeadCount());
    }

    [Fact]
    public void MarkDeadMovesRowToDead()
    {
        var id = Enqueue(_store);
        _store.MarkDead(id, "HTTP 404");
        Assert.Equal(0, _store.PendingCount());
        Assert.Equal(1, _store.DeadCount());
        Assert.Empty(_store.GetDue(10));
    }

    [Fact]
    public void EnqueuePreservesQueryAndPayload()
    {
        const string query = "?thread_id=123";
        const string payload = @"{""content"":""x""}";
        var id = _store.Enqueue("111111111111111111", "tok", query, payload);
        var job = Assert.Single(_store.GetDue(10));
        Assert.Equal(id, job.Id);
        Assert.Equal(query, job.Query);
        Assert.Equal(payload, job.Payload);
        Assert.Equal(0, job.Attempts);
    }

    [Fact]
    public void GetDueRespectsLimit()
    {
        // One head job per webhook: three webhooks, limit two.
        Enqueue(_store, "111111111111111111");
        Enqueue(_store, "222222222222222222");
        Enqueue(_store, "333333333333333333");
        Assert.Equal(2, _store.GetDue(2).Count);
    }

    [Fact]
    public void FuturePostponeHidesJobUntilDue()
    {
        var id = Enqueue(_store);
        _store.Postpone(id, DateTime.UtcNow.AddHours(1), null);
        Assert.Empty(_store.GetDue(10));
        Assert.Equal(1, _store.PendingCount());
    }

    [Fact]
    public void DeleteMissingIdIsNoOp()
    {
        Enqueue(_store);
        _store.Delete(999_999_999);
        Assert.Equal(1, _store.PendingCount());
    }

    [Fact]
    public void DeleteRemovesDeliveredJob()
    {
        var id = Enqueue(_store);
        _store.Delete(id);
        Assert.Equal(0, _store.PendingCount());
    }
}
