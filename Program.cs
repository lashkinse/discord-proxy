using DiscordProxy;
using DiscordProxy.Data;
using DiscordProxy.Endpoints;
using DiscordProxy.Services;

// Entry point — composition only: settings, services, routes. No logic here.
var builder = WebApplication.CreateBuilder(args);

var options = ProxyOptions.FromEnvironment();
builder.WebHost.UseUrls($"http://0.0.0.0:{options.Port}");
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<QueueStore>();
builder.Services.AddHttpClient(DiscordSender.HttpClientName, client =>
{
    client.Timeout = options.DiscordTimeout;
    client.DefaultRequestHeaders.UserAgent.ParseAdd("DiscordProxy/1.0");
});
builder.Services.AddSingleton<DiscordSender>();
builder.Services.AddHostedService<WebhookWorker>();

var app = builder.Build();
app.MapWebhookEndpoints();
app.Run();
