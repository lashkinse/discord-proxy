using DiscordProxy;
using DiscordProxy.Data;
using DiscordProxy.Endpoints;
using DiscordProxy.Services;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

// Entry point — composition only: settings, services, routes. No logic here.
var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, logger) =>
{
    // Full control from JSON when present; sane defaults otherwise.
    // (Branching avoids double sinks: config and code never stack.)
    if (context.Configuration.GetSection("Serilog").Exists())
    {
        logger.ReadFrom.Configuration(context.Configuration);
        return;
    }

    logger
        .MinimumLevel.Warning()
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");
});

builder.Services.AddOptions<ProxyOptions>()
    .Bind(builder.Configuration.GetSection(ProxyOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
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
builder.Services.AddSingleton<IDiscordSender>(sp => sp.GetRequiredService<DiscordSender>());
builder.Services.AddHostedService<WebhookWorker>();

var app = builder.Build();
var appOptions = app.Services.GetRequiredService<ProxyOptions>();
app.Urls.Clear();
app.Urls.Add($"http://0.0.0.0:{appOptions.Port}");
app.Logger.LogInformation("Listening on port {Port}, database {Db}", appOptions.Port, appOptions.GetDbPath());
app.UseSerilogRequestLogging();
app.MapWebhookEndpoints();
app.Run();

/// <summary>Exposed for integration tests (WebApplicationFactory).</summary>
public partial class Program { }
