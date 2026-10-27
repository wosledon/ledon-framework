using System.Text.Json;
using Ledon.ConfigCenterDemo;
using Ledon.Settings;
using Ledon.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
var settingsPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "config-center.json");
Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);

var bridge = new SettingsConfigurationBridge();
((IConfigurationBuilder)builder.Configuration).Add(new SettingsConfigurationSource(bridge));

builder.AddLedonWeb(options =>
{
    options.UseOpenApi = true;
    options.UseScalarUi = true;
});

builder.Services.AddSingleton(bridge);
builder.Services.AddLedonSettings(options => options.StorePath = settingsPath);
builder.Services.Configure<FeatureOptions>(builder.Configuration.GetSection("Feature"));

var app = builder.Build();
bridge.Settings = app.Services.GetRequiredService<ISettingsService>();
await SeedAsync(bridge.Settings, app.Logger);
bridge.Reload();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseLedonWeb();

var configGroup = app.MapGroup("/api/config");

configGroup.MapGet("/", async (ISettingsService settings, CancellationToken ct) =>
{
    var all = await settings.GetAllAsync<JsonElement>(SettingsConfigurationProvider.Prefix, ct);

    return Results.Ok(all.Select(item => new
    {
        key = item.Key[SettingsConfigurationProvider.Prefix.Length..],
        value = item.Value,
    }));
});

configGroup.MapGet("/{**key}", async (string key, ISettingsService settings, CancellationToken ct) =>
{
    var value = await settings.GetAsync<JsonElement>(SettingsConfigurationProvider.Prefix + key, ct);

    return value.ValueKind == JsonValueKind.Undefined
        ? Results.NotFound()
        : Results.Ok(new { key, value });
});

configGroup.MapPut("/{**key}", async (
    string key,
    JsonElement body,
    ISettingsService settings,
    SettingsConfigurationBridge bridge,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(key) || !key.Contains(':', StringComparison.Ordinal))
        return Results.BadRequest(new { error = "key 建议使用 模块:键 形式" });

    await settings.SetAsync(
        SettingsConfigurationProvider.Prefix + key,
        JsonDocument.Parse(body.GetRawText()).RootElement,
        updatedBy: "config-center-demo",
        cancellationToken: ct);

    bridge.Reload();
    return Results.Ok(new { key, value = body });
});

configGroup.MapDelete("/{**key}", async (string key, ISettingsService settings, SettingsConfigurationBridge bridge, CancellationToken ct) =>
{
    var removed = await settings.RemoveAsync(SettingsConfigurationProvider.Prefix + key, ct);

    if (removed)
        bridge.Reload();

    return removed ? Results.NoContent() : Results.NotFound();
});

app.MapGet("/api/effective", (IConfiguration configuration, IOptionsMonitor<FeatureOptions> feature) => Results.Ok(new
{
    source = "IConfiguration + IOptionsMonitor",
    greeting = configuration["Feature:WelcomeMessage"],
    maxItems = configuration["Feature:MaxItems"],
    options = feature.CurrentValue,
    note = "PUT /api/config/Feature:WelcomeMessage 后再次请求，值会立即变化",
}));

app.MapGet("/api/info", () => new
{
    message = "Ledon Config Center Demo",
    store = "Ledon.Settings JSON file + custom IConfigurationProvider",
    endpoints = new[]
    {
        "GET /api/config",
        "GET /api/config/Feature:WelcomeMessage",
        "PUT /api/config/Feature:WelcomeMessage（body 为 JSON 值，如 \"hello\"）",
        "DELETE /api/config/Feature:WelcomeMessage",
        "GET /api/effective",
    },
});

app.MapFallbackToFile("index.html");
app.Run();

static async Task SeedAsync(ISettingsService settings, ILogger logger)
{
    var defaults = new Dictionary<string, string>
    {
        ["Feature:WelcomeMessage"] = "\"Hello from config center\"",
        ["Feature:MaxItems"] = "20",
        ["App:Name"] = "\"Ledon Config Center\"",
    };

    foreach (var (key, json) in defaults)
    {
        var fullKey = SettingsConfigurationProvider.Prefix + key;
        var current = await settings.GetAsync<JsonElement>(fullKey);

        if (current.ValueKind != JsonValueKind.Undefined)
            continue;

        await settings.SetAsync(fullKey, JsonDocument.Parse(json).RootElement);
        logger.LogInformation("已播种默认配置：{Key} = {Value}", key, json);
    }
}

public sealed class FeatureOptions
{
    public string WelcomeMessage { get; set; } = "default";
    public int MaxItems { get; set; } = 10;
}

public sealed class DemoModule : global::Ledon.Module.Abstractions.IModule
{
    public Task OnInitializedAsync(global::Ledon.Module.Abstractions.ModuleContext context, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
