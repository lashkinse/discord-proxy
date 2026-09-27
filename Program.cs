using DiscordProxy;
using DiscordProxy.Data;
using DiscordProxy.Endpoints;
using DiscordProxy.Services;
using Microsoft.Extensions.Options;

// Entry point — composition only: settings, services, routes. No logic here.
var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ProxyOptions>(builder.Configuration.GetSection(ProxyOptions.SectionName));
builder.Services.PostConfigure<ProxyOptions>(options =>
{
    // Legacy overrides: plain PORT / DB_PATH win over the config file.
    if (int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var port) && port is > 0 and <= 65535)
        options.Port = port;
    var dbPath = Environment.GetEnvironmentVariable("DB_PATH");
    if (!string.IsNullOrWhiteSpace(dbPath))
        options.DbPath = dbPath;
});
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<ProxyOptions>>().Value);
builder.Services.AddSingleton<QueueStore>();
builder.Services.AddHttpClient(DiscordSender.HttpClientName, (sp, client) =>
{
    var options = sp.GetRequiredService<ProxyOptions>();
    client.Timeout = options.DiscordTimeout;
    client.DefaultRequestHeaders.UserAgent.ParseAdd("DiscordProxy/1.0");
});
builder.Services.AddSingleton<DiscordSender>();
builder.Services.AddHostedService<WebhookWorker>();

var app = builder.Build();
var appOptions = app.Services.GetRequiredService<ProxyOptions>();
app.Urls.Clear();
app.Urls.Add($"http://0.0.0.0:{appOptions.Port}");
app.Logger.LogInformation("Listening on port {Port}, database {Db}", appOptions.Port, appOptions.GetDbPath());
app.MapWebhookEndpoints();
app.Run();
