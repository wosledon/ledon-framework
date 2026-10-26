using System.Collections.Concurrent;
using System.Text.Json;

namespace Ledon.Secrets;

/// <summary>密钥提供器。</summary>
public interface ISecretProvider
{
    /// <summary>按名称取密钥；不存在返回 null。名称格式由实现定义（Vault：<c>path#field</c>）。</summary>
    Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>读取一个路径下的全部字段（配置源用）；默认实现返回空。</summary>
    Task<IReadOnlyDictionary<string, string>> GetSecretsAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
}

/// <summary>密钥读取扩展。</summary>
public static class SecretProviderExtensions
{
    public static async Task<string> GetRequiredSecretAsync(
        this ISecretProvider provider,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        return await provider.GetSecretAsync(name, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"密钥不存在：{name}");
    }
}

/// <summary>密钥读取异常。</summary>
public class SecretProviderException(string message) : Exception(message);

/// <summary>HashiCorp Vault 配置。</summary>
public sealed class VaultOptions
{
    /// <summary>Vault 地址，如 https://vault.example.com:8200。</summary>
    public string? Address { get; set; }

    /// <summary>Vault Token（也可用 <see cref="TokenFile"/> / 环境变量 VAULT_TOKEN）。</summary>
    public string? Token { get; set; }

    /// <summary>从文件读取 Token（Kubernetes ServiceAccount 场景）。</summary>
    public string? TokenFile { get; set; }

    /// <summary>KV 挂载点，默认 secret。</summary>
    public string MountPoint { get; set; } = "secret";

    /// <summary>是否 KV v2，默认 <c>true</c>。</summary>
    public bool KvV2 { get; set; } = true;

    /// <summary>Vault Enterprise 命名空间（可选）。</summary>
    public string? Namespace { get; set; }

    /// <summary>需要加载的路径列表（配置源用）。</summary>
    public List<string> Paths { get; set; } = [];

    /// <summary>配置键是否包含路径前缀（多路径时防冲突），默认 <c>false</c>。</summary>
    public bool IncludePathInKey { get; set; }

    /// <summary>配置键前缀（多个路径时避免冲突）。</summary>
    public string? KeyPrefix { get; set; }

    /// <summary>缓存时长，默认 5 分钟；<see cref="TimeSpan.Zero"/> 关闭缓存。</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Vault 不可用时是否跳过（配置源启动容错），默认 <c>false</c>。</summary>
    public bool Optional { get; set; }
}

/// <summary>HashiCorp Vault KV 提供器。</summary>
public sealed class VaultSecretProvider : ISecretProvider, IDisposable
{
    public const string HttpClientName = "Ledon.Secrets.Vault";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly VaultOptions _options;

    public VaultSecretProvider(VaultOptions options, HttpClient httpClient)
    {
        _options = options;
        _httpClient = httpClient;
        _ownsHttpClient = false;
    }

    /// <summary>创建独立 HttpClient（配置源启动时用，提供器负责释放）。</summary>
    public VaultSecretProvider(VaultOptions options)
        : this(options, new HttpClient())
    {
        _ownsHttpClient = true;
    }

    public VaultSecretProvider(VaultOptions options, IHttpClientFactory httpClientFactory)
        : this(options, httpClientFactory.CreateClient(HttpClientName))
    {
        _ownsHttpClient = true;
    }

    public async Task<string?> GetSecretAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var (path, field) = SplitReference(name);
        var secrets = await GetSecretsAsync(path, cancellationToken).ConfigureAwait(false);

        return secrets.TryGetValue(field, out var value) ? value : null;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetSecretsAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        var url = BuildUrl(path);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("X-Vault-Token", ResolveToken());

        if (!string.IsNullOrWhiteSpace(_options.Namespace))
            request.Headers.TryAddWithoutValidation("X-Vault-Namespace", _options.Namespace);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return new Dictionary<string, string>();

        if (!response.IsSuccessStatusCode)
            throw new SecretProviderException($"Vault 读取失败（{(int)response.StatusCode}）：{path}");

        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(payload);

        if (!document.RootElement.TryGetProperty("data", out var data))
            throw new SecretProviderException("Vault 响应缺少 data");

        if (_options.KvV2)
        {
            if (!data.TryGetProperty("data", out data))
                throw new SecretProviderException("Vault KV v2 响应缺少 data.data");
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        if (data.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in data.EnumerateObject())
                result[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.GetRawText();
        }

        return result;
    }

    private string BuildUrl(string path)
    {
        var address = _options.Address!.TrimEnd('/');
        var encodedPath = string.Join('/', path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));

        var mount = Uri.EscapeDataString(_options.MountPoint.Trim('/'));

        return _options.KvV2
            ? $"{address}/v1/{mount}/data/{encodedPath}"
            : $"{address}/v1/{mount}/{encodedPath}";
    }

    private string ResolveToken()
    {
        if (!string.IsNullOrWhiteSpace(_options.Token))
            return _options.Token;

        if (!string.IsNullOrWhiteSpace(_options.TokenFile) && File.Exists(_options.TokenFile))
            return File.ReadAllText(_options.TokenFile).Trim();

        return Environment.GetEnvironmentVariable("VAULT_TOKEN") ?? string.Empty;
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.Address))
            throw new InvalidOperationException("VaultOptions.Address 未配置");

        if (string.IsNullOrWhiteSpace(ResolveToken()))
            throw new InvalidOperationException("Vault Token 未配置（Token/TokenFile/VAULT_TOKEN）");
    }

    private static (string Path, string Field) SplitReference(string name)
    {
        var separator = name.LastIndexOf('#');

        return separator > 0
            ? (name[..separator], name[(separator + 1)..])
            : (name, "value");
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}

/// <summary>带 TTL 缓存的密钥提供器。</summary>
public sealed class CachingSecretProvider(
    ISecretProvider inner,
    TimeProvider timeProvider,
    VaultOptions options) : ISecretProvider
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CacheEntry> _cache =
        new(StringComparer.Ordinal);

    public async Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default)
    {
        if (options.CacheDuration <= TimeSpan.Zero)
            return await inner.GetSecretAsync(name, cancellationToken).ConfigureAwait(false);

        var now = timeProvider.GetUtcNow();

        if (_cache.TryGetValue(name, out var entry) && entry.ExpiresAtUtc > now)
            return entry.Value;

        var value = await inner.GetSecretAsync(name, cancellationToken).ConfigureAwait(false);
        _cache[name] = new CacheEntry(value, now + options.CacheDuration);

        return value;
    }

    private sealed record CacheEntry(string? Value, DateTimeOffset ExpiresAtUtc);
}

/// <summary>内存密钥提供器（开发/测试；生产用 Vault 或 KMS）。</summary>
public sealed class InMemorySecretProvider : ISecretProvider
{
    private readonly ConcurrentDictionary<string, string> _secrets;

    public InMemorySecretProvider(IEnumerable<KeyValuePair<string, string>>? initial = null)
    {
        _secrets = new ConcurrentDictionary<string, string>(initial ?? [], StringComparer.Ordinal);
    }

    public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return Task.FromResult(_secrets.TryGetValue(name, out var value) ? value : null);
    }

    public void Set(string name, string value) => _secrets[name] = value;

    public bool Remove(string name) => _secrets.TryRemove(name, out _);
}
