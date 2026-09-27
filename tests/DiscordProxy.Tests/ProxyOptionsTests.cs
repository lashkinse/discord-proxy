using DiscordProxy;

namespace DiscordProxy.Tests;

public sealed class ProxyOptionsTests
{
    [Fact]
    public void AbsoluteDbPathPassesThrough()
    {
        var options = new ProxyOptions { DbPath = "/var/lib/proxy/queue.db" };
        var resolved = options.GetDbPath().Replace('\\', '/');
        Assert.Equal("/var/lib/proxy/queue.db", resolved);
    }

    [Fact]
    public void RelativeDbPathResolvesAgainstAppDirectory()
    {
        var options = new ProxyOptions { DbPath = "queue.db" };
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "queue.db"), options.GetDbPath());
    }

    [Fact]
    public void ComputedTimeoutsFollowSeconds()
    {
        var options = new ProxyOptions { PaceSeconds = 3, DiscordTimeoutSeconds = 20 };
        Assert.Equal(TimeSpan.FromSeconds(3), options.PacePerWebhook);
        Assert.Equal(TimeSpan.FromSeconds(20), options.DiscordTimeout);
    }
}
