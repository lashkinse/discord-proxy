using DiscordProxy.Services;

namespace DiscordProxy.Tests;

public sealed class SenderParsingTests
{
    [Theory]
    [InlineData(@"{""retry_after"":1.059,""global"":false}", 1.059)]
    [InlineData(@"{""retry_after"":2}", 2.0)]
    [InlineData(@"{""retry_after"":0}", 0)]
    [InlineData(@"{""retry_after"":""1.5""}", 5.0)] // wrong type
    [InlineData(@"{""retry_after"":3599.9}", 3599.9)]
    [InlineData(@"{""retry_after"":3600}", 5.0)] // out of range
    [InlineData(@"{""message"":""You are being rate limited.""}", 5.0)] // fallback
    [InlineData(@"not json", 5.0)] // fallback
    [InlineData(@"{""retry_after"":-1}", 5.0)] // out of range
    [InlineData(@"{""retry_after"":99999}", 5.0)] // out of range
    public void RetryAfterParsing(string body, double expected) =>
        Assert.Equal(expected, DiscordSender.ParseRetryAfter(body));

    [Theory]
    [InlineData(@"{""global"":true}", true)]
    [InlineData(@"{""global"":false}", false)]
    [InlineData(@"{""global"":""true""}", false)] // wrong type
    [InlineData(@"{""global"":1}", false)] // wrong type
    [InlineData(@"{}", false)]
    [InlineData(@"not json", false)]
    public void GlobalFlagParsing(string body, bool expected) =>
        Assert.Equal(expected, DiscordSender.IsGlobal(body));
}
