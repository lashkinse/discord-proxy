using System.Net;
using DiscordProxy.Models;
using DiscordProxy.Services;

namespace DiscordProxy.Tests;

/// <summary>
/// Sender classification against a stub transport: no network.
/// </summary>
public sealed class SenderTransportTests
{
    private sealed class StatusHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public StatusHandler(HttpStatusCode status, string body = "")
        {
            _status = status;
            _body = body;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json")
            });
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        private readonly TimeSpan _timeout;
        public StubFactory(HttpMessageHandler handler, TimeSpan timeout)
        {
            _handler = handler;
            _timeout = timeout;
        }
        public HttpClient CreateClient(string name) =>
            new(_handler, disposeHandler: false) { Timeout = _timeout };
    }

    private static QueuedJob Job() => new(1, "111111111111111111", "token", "", @"{""content"":""x""}", 0);

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task SuccessMapsToDelivered(HttpStatusCode status)
    {
        var sender = new DiscordSender(new StubFactory(new StatusHandler(status), TimeSpan.FromSeconds(5)));
        var outcome = await sender.SendAsync(Job(), CancellationToken.None);
        var delivered = Assert.IsType<Delivered>(outcome);
        Assert.Equal((int)status, delivered.StatusCode);
    }

    [Fact]
    public async Task RateLimitBodyMapsToRateLimited()
    {
        var sender = new DiscordSender(new StubFactory(
            new StatusHandler(HttpStatusCode.TooManyRequests, @"{""retry_after"":1.5,""global"":true}"),
            TimeSpan.FromSeconds(5)));
        var outcome = await sender.SendAsync(Job(), CancellationToken.None);
        var limited = Assert.IsType<RateLimited>(outcome);
        Assert.Equal(1.5, limited.RetryAfterSeconds);
        Assert.True(limited.IsGlobal);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task ServerErrorMapsToRetryable(HttpStatusCode status)
    {
        var sender = new DiscordSender(new StubFactory(new StatusHandler(status), TimeSpan.FromSeconds(5)));
        Assert.IsType<Retryable>(await sender.SendAsync(Job(), CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ClientErrorMapsToPermanent(HttpStatusCode status)
    {
        var sender = new DiscordSender(new StubFactory(new StatusHandler(status), TimeSpan.FromSeconds(5)));
        Assert.IsType<Permanent>(await sender.SendAsync(Job(), CancellationToken.None));
    }

    [Fact]
    public async Task TimeoutMapsToRetryable()
    {
        var sender = new DiscordSender(new StubFactory(new SlowHandler(), TimeSpan.FromSeconds(1)));
        Assert.IsType<Retryable>(await sender.SendAsync(Job(), CancellationToken.None));
    }
}
