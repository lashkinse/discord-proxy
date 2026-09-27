using DiscordProxy.Services;

namespace DiscordProxy.Tests;

public sealed class DeliveryPacerTests
{
    [Fact]
    public void FirstSendNeverWaits()
    {
        var pacer = new DeliveryPacer(TimeSpan.FromSeconds(2));
        Assert.False(pacer.ShouldWait("w1", DateTime.UtcNow, out _));
    }

    [Fact]
    public void ImmediateResendWaitsUntilIntervalPasses()
    {
        var pacer = new DeliveryPacer(TimeSpan.FromSeconds(2));
        var now = DateTime.UtcNow;
        pacer.MarkSent("w1", now);

        Assert.True(pacer.ShouldWait("w1", now.AddSeconds(1), out var notBefore));
        Assert.Equal(now.AddSeconds(2), notBefore);
        Assert.False(pacer.ShouldWait("w1", now.AddSeconds(2.1), out _));
    }

    [Fact]
    public void KeysAreIndependent()
    {
        var pacer = new DeliveryPacer(TimeSpan.FromSeconds(60));
        pacer.MarkSent("w1", DateTime.UtcNow);
        Assert.False(pacer.ShouldWait("w2", DateTime.UtcNow, out _));
    }

    [Fact]
    public void IdleEntriesAreTrimmed()
    {
        var pacer = new DeliveryPacer(TimeSpan.FromSeconds(60));
        var old = DateTime.UtcNow.AddHours(-2);
        for (var i = 0; i < 128; i++)
            pacer.MarkSent("w" + i, old);

        Assert.False(pacer.ShouldWait("w0", DateTime.UtcNow, out _));
    }
}
