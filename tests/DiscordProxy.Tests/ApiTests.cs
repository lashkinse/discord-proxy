using System.Net;
using System.Net.Http.Json;
using DiscordProxy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DiscordProxy.Tests;

/// <summary>
/// Full pipeline through a real server: 202 intake, validation,
/// background worker delivery attempts. Uses a fake webhook so nothing
/// reaches a real Discord channel; Discord answers 401 and the job goes dead.
/// </summary>
public sealed class ApiTests : IAsyncLifetime
{
    private const string FakeId = "111111111111111111";
    private const string FakeToken = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"proxy-api-{Guid.NewGuid():N}.db");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("Proxy:DbPath", _dbPath));
        _client = _factory.CreateClient();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            try { File.Delete(_dbPath + suffix); } catch { }
        }
    }

    [Fact]
    public async Task PostIsAcceptedImmediately()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/webhooks/{FakeId}/{FakeToken}", new { content = "hello" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task UndeliverableJobEndsUpDeadWithoutSpam()
    {
        await _client.PostAsJsonAsync($"/api/webhooks/{FakeId}/{FakeToken}", new { content = "hello" });

        // Discord answers 401 to the fake token; the worker buries it after one try.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var stats = await _client.GetFromJsonAsync<Dictionary<string, long>>("/stats");
            if (stats!["pending"] == 0 && stats["dead"] == 1)
                return;
            await Task.Delay(500);
        }
        Assert.Fail("Job was not buried in time.");
    }

    [Theory]
    [InlineData(@"{""content"":""""}")]
    [InlineData(@"{}")]
    public async Task EmptyPayloadIsRejected(string json)
    {
        var response = await _client.PostAsync(
            $"/api/webhooks/{FakeId}/{FakeToken}",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MalformedJsonIsRejected()
    {
        var response = await _client.PostAsync(
            $"/api/webhooks/{FakeId}/{FakeToken}",
            new StringContent("{oops", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BadIdIsRejected()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/webhooks/abc/{FakeToken}", new { content = "hello" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task WrongContentTypeIsRejected()
    {
        var response = await _client.PostAsync(
            $"/api/webhooks/{FakeId}/{FakeToken}",
            new StringContent(@"{""content"":""hello""}", System.Text.Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OversizeBodyIsRejectedWith413()
    {
        var big = new string('y', 70_000);
        var response = await _client.PostAsync(
            $"/api/webhooks/{FakeId}/{FakeToken}",
            new StringContent(@"{""content"":""" + big + @"""}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task RealWebhookInfoPassesThrough()
    {
        // Read-only: hits the real Discord API but posts nothing.
        const string realId = "1553762945471742045";
        const string realToken = "NHIEjMFXM6uiWVISvC19up4iY9CI2Eoag4qCa8az5U9bsBu3o7n4l4It307iH52SAHt-";
        var response = await _client.GetAsync($"/api/webhooks/{realId}/{realToken}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains($"\"id\":\"{realId}\"", body);
    }

    [Fact]
    public async Task HealthAndStatsAnswer()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health/ready")).StatusCode);

        var stats = await _client.GetFromJsonAsync<Dictionary<string, long>>("/stats");
        Assert.NotNull(stats);
        Assert.True(stats.ContainsKey("pending"));
        Assert.True(stats.ContainsKey("dead"));
    }

    [Fact]
    public async Task ParallelPostsAreAllAccepted()
    {
        // 20 concurrent posts: SQLite + worker must not lose or corrupt anything.
        var tasks = Enumerable.Range(0, 20).Select(_ =>
            _client.PostAsJsonAsync($"/api/webhooks/{FakeId}/{FakeToken}", new { content = "x" }));
        var responses = await Task.WhenAll(tasks);
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));

        // Fake token → Discord 401s everything; nothing may stay pending.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var stats = await _client.GetFromJsonAsync<Dictionary<string, long>>("/stats");
            if (stats!["pending"] == 0 && stats["dead"] == 20)
                return;
            await Task.Delay(500);
        }
        Assert.Fail("Parallel jobs were not buried in time.");
    }

    [Fact]
    public void InvalidConfigFailsFast()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("Proxy:Port", "99999"));
        var ex = Assert.ThrowsAny<Exception>(() => factory.Services.GetRequiredService<ProxyOptions>());
        Assert.Contains("Port", ex.Message);
    }
}
