using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

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
}
