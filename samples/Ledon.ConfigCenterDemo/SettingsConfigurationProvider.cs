using System.Text.Json;
using Ledon.Settings;
using Microsoft.Extensions.Configuration;

namespace Ledon.ConfigCenterDemo;

/// <summary>
/// 把 Ledon.Settings 的 JSON 设置映射成 IConfiguration：
/// 写入设置 → bridge.Reload() → IConfiguration/IOptionsMonitor 立即热更新。
/// </summary>
public sealed class SettingsConfigurationSource(SettingsConfigurationBridge bridge) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        var provider = new SettingsConfigurationProvider(bridge);
        bridge.Provider = provider;
        return provider;
    }
}

public sealed class SettingsConfigurationBridge
{
    public ISettingsService? Settings { get; set; }

    public SettingsConfigurationProvider? Provider { get; set; }

    public void Reload() => Provider?.Load();
}

public sealed class SettingsConfigurationProvider(SettingsConfigurationBridge bridge) : ConfigurationProvider
{
    public const string Prefix = "config:";

    public override void Load()
    {
        if (bridge.Settings is null)
            return;

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in bridge.Settings
            .GetAllAsync<JsonElement>(Prefix, CancellationToken.None)
            .GetAwaiter()
            .GetResult())
        {
            var path = key[Prefix.Length..];
            Flatten(path, value, values);
        }

        Data = values;
        OnReload();
    }

    private static void Flatten(string path, JsonElement value, IDictionary<string, string?> target)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
                Flatten($"{path}:{property.Name}", property.Value, target);

            return;
        }

        target[path] = value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.GetRawText();
    }
}
