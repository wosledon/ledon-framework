using System.Collections.Concurrent;

namespace Ledon.ApiClients;

/// <summary>单个出站 API 客户端配置。</summary>
public sealed class ApiClientOptions
{
    /// <summary>基础地址，如 https://api.example.com/。</summary>
    public string? BaseAddress { get; set; }

    /// <summary>OAuth2 client_credentials 客户端 ID；为空表示不自动取令牌。</summary>
    public string? ClientId { get; set; }

    /// <summary>OAuth2 客户端密钥。</summary>
    public string? ClientSecret { get; set; }

    /// <summary>OAuth2 令牌端点（绝对地址）。</summary>
    public string? TokenEndpoint { get; set; }

    /// <summary>OAuth2 scope（可选，空格分隔）。</summary>
    public string? Scope { get; set; }

    /// <summary>提前刷新令牌的时间，默认 1 分钟。</summary>
    public TimeSpan TokenRefreshSkew { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>请求超时，默认 30 秒。</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>关联 ID 请求头，默认 X-Correlation-ID。</summary>
    public string CorrelationHeaderName { get; set; } = "X-Correlation-ID";

    /// <summary>是否附加 traceparent，默认 <c>true</c>。</summary>
    public bool PropagateTraceParent { get; set; } = true;

    /// <summary>默认请求头（每个请求都会附加）。</summary>
    public Dictionary<string, string> DefaultHeaders { get; set; } = [];
}

/// <summary>客户端注册表（按名称保存配置与令牌提供器）。</summary>
public sealed class ApiClientRegistry
{
    internal ConcurrentDictionary<string, ApiClientOptions> Clients { get; } = new(StringComparer.Ordinal);
    internal ConcurrentDictionary<string, IClientCredentialsTokenProvider> Providers { get; } = new(StringComparer.Ordinal);

    public bool TryGet(string name, out ApiClientOptions options) => Clients.TryGetValue(name, out options!);
}

/// <summary>OAuth2 client_credentials 令牌提供器（带缓存与提前刷新）。</summary>
public interface IClientCredentialsTokenProvider
{
    /// <summary>取令牌；未配置客户端凭据时返回 null。</summary>
    Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>清空缓存（令牌被服务端吊销/更换密钥时调用）。</summary>
    void Invalidate();
}

/// <summary>默认 client_credentials 令牌提供器（进程内缓存，多实例不共享）。</summary>
public sealed class ClientCredentialsTokenProvider(
    ApiClientOptions options,
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider) : IClientCredentialsTokenProvider, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAtUtc;

    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return null;

        if (_token is not null && timeProvider.GetUtcNow() < _expiresAtUtc)
            return _token;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_token is not null && timeProvider.GetUtcNow() < _expiresAtUtc)
                return _token;

            var form = new List<KeyValuePair<string, string>>
            {
                new("grant_type", "client_credentials"),
                new("client_id", options.ClientId!),
                new("client_secret", options.ClientSecret!),
            };

            if (!string.IsNullOrWhiteSpace(options.Scope))
                form.Add(new("scope", options.Scope));

            using var request = new HttpRequestMessage(HttpMethod.Post, options.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(form),
            };

            using var client = httpClientFactory.CreateClient();
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"获取客户端令牌失败（{(int)response.StatusCode}）：{Truncate(payload, 512)}");
            }

            using var document = System.Text.Json.JsonDocument.Parse(payload);

            if (!document.RootElement.TryGetProperty("access_token", out var accessToken)
                || string.IsNullOrWhiteSpace(accessToken.GetString()))
            {
                throw new InvalidOperationException("令牌响应缺少 access_token");
            }

            var expiresIn = document.RootElement.TryGetProperty("expires_in", out var expires)
                && expires.TryGetInt32(out var seconds)
                    ? seconds
                    : 3600;

            _token = accessToken.GetString();
            _expiresAtUtc = timeProvider.GetUtcNow()
                .AddSeconds(expiresIn)
                .Subtract(options.TokenRefreshSkew);

            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate()
    {
        _token = null;
        _expiresAtUtc = default;
    }

    private bool IsConfigured =>
        !string.IsNullOrWhiteSpace(options.ClientId)
        && !string.IsNullOrWhiteSpace(options.ClientSecret)
        && !string.IsNullOrWhiteSpace(options.TokenEndpoint);

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length];

    public void Dispose() => _gate.Dispose();
}

/// <summary>关联 ID / traceparent 传播 + Bearer 令牌处理器。</summary>
public sealed class ApiClientAuthHandler(
    IClientCredentialsTokenProvider tokenProvider,
    ApiClientOptions options,
    Microsoft.AspNetCore.Http.IHttpContextAccessor? httpContextAccessor = null) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = await tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);

        if (token is not null && request.Headers.Authorization is null)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        ApplyCorrelation(request);

        foreach (var (name, value) in options.DefaultHeaders)
        {
            if (!request.Headers.Contains(name))
                request.Headers.TryAddWithoutValidation(name, value);
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private void ApplyCorrelation(HttpRequestMessage request)
    {
        var httpContext = httpContextAccessor?.HttpContext;

        if (!request.Headers.Contains(options.CorrelationHeaderName))
        {
            var correlationId = httpContext?.Request.Headers[options.CorrelationHeaderName].FirstOrDefault()
                ?? httpContext?.TraceIdentifier
                ?? Guid.NewGuid().ToString("N");

            request.Headers.TryAddWithoutValidation(options.CorrelationHeaderName, correlationId);
        }

        if (options.PropagateTraceParent
            && System.Diagnostics.Activity.Current?.Id is { } traceParent
            && !request.Headers.Contains("traceparent"))
        {
            request.Headers.TryAddWithoutValidation("traceparent", traceParent);
        }
    }
}
