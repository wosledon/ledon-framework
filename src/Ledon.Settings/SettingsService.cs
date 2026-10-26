using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ledon.Settings;

/// <summary>强类型设置服务：JSON 序列化 + 可选 Data Protection 加密。</summary>
public sealed class SettingsService : ISettingsService
{
    private const string CipherPrefix = "dp:v1:";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ISettingsStore _store;
    private readonly IDataProtector _protector;

    public SettingsService(
        ISettingsStore store,
        LedonSettingsOptions options,
        IDataProtectionProvider protectionProvider)
    {
        _store = store;
        _protector = protectionProvider.CreateProtector(options.EncryptionPurpose);
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var entry = await _store.GetAsync(key, cancellationToken).ConfigureAwait(false);

        if (entry is null)
            return default;

        return Deserialize<T>(Unprotect(entry));
    }

    public async Task<T> GetRequiredAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        var value = await GetAsync<T>(key, cancellationToken).ConfigureAwait(false);

        return value ?? throw new KeyNotFoundException($"设置不存在：{key}");
    }

    public async Task SetAsync<T>(
        string key,
        T value,
        bool encrypted = false,
        string? updatedBy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var json = JsonSerializer.Serialize(value, JsonOptions);

        if (encrypted)
            json = CipherPrefix + _protector.Protect(json);

        await _store.SetAsync(new SettingEntry
        {
            Key = key,
            Value = json,
            Encrypted = encrypted,
            UpdatedBy = updatedBy,
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _store.RemoveAsync(key, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, T>> GetAllAsync<T>(
        string? prefix = null,
        CancellationToken cancellationToken = default)
    {
        var entries = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, T>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (!string.IsNullOrEmpty(prefix)
                && !entry.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = Deserialize<T>(Unprotect(entry));

            if (value is not null)
                result[entry.Key] = value;
        }

        return result;
    }

    private static T? Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions);

    private string Unprotect(SettingEntry entry)
    {
        if (!entry.Encrypted)
            return entry.Value;

        if (!entry.Value.StartsWith(CipherPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"设置 {entry.Key} 标记为加密但格式无效");

        try
        {
            return _protector.Unprotect(entry.Value[CipherPrefix.Length..]);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"设置 {entry.Key} 解密失败：Data Protection 密钥环是否与写入时一致？", ex);
        }
    }
}

/// <summary>运行时设置注册。</summary>
public static class LedonSettingsServiceCollectionExtensions
{
    /// <summary>
    /// 注册运行时设置：
    /// <code>
    /// builder.Services.AddLedonSettings(o =&gt;
    /// {
    ///     o.StorePath = "App_Data/settings.json";   // 留空则用内存存储
    /// });
    /// </code>
    /// </summary>
    public static IServiceCollection AddLedonSettings(
        this IServiceCollection services,
        Action<LedonSettingsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new LedonSettingsOptions();
        configure?.Invoke(options);

        services.AddDataProtection();
        services.TryAddSingleton(options);

        if (string.IsNullOrWhiteSpace(options.StorePath))
            services.TryAddSingleton<ISettingsStore, InMemorySettingsStore>();
        else
            services.TryAddSingleton<ISettingsStore>(_ => new JsonFileSettingsStore(options.StorePath));

        services.TryAddSingleton<ISettingsService, SettingsService>();

        return services;
    }
}
