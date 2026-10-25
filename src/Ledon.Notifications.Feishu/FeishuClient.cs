using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Ledon.Notifications.Feishu;

/// <summary>飞书开放平台客户端（发消息 + 换取 tenant_access_token）。</summary>
public sealed class FeishuClient : IDisposable
{
    private readonly FeishuOptions _options;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);

    private string? _tenantAccessToken;
    private DateTimeOffset _tokenExpiresAt;

    public FeishuClient(FeishuOptions options, HttpClient? httpClient = null)
    {
        _options = options;
        _http = httpClient ?? new HttpClient { BaseAddress = new Uri(options.BaseUrl) };
    }

    /// <summary>发文本到自定义机器人（群通知最简方式）。</summary>
    public async Task<string> SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        var timestamp = FeishuCrypto.TimestampNow();
        var payload = new Dictionary<string, object?>
        {
            ["msg_type"] = "text",
            ["content"] = new { text },
        };

        if (!string.IsNullOrWhiteSpace(_options.WebhookSecret))
        {
            payload["timestamp"] = timestamp.ToString();
            payload["sign"] = FeishuCrypto.SignWebhook(timestamp, _options.WebhookSecret);
        }

        return await PostWebhookAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>发交互卡片到自定义机器人。</summary>
    public async Task<string> SendCardAsync(object card, CancellationToken cancellationToken = default)
    {
        var timestamp = FeishuCrypto.TimestampNow();
        var payload = new Dictionary<string, object?>
        {
            ["msg_type"] = "interactive",
            ["card"] = card,
        };

        if (!string.IsNullOrWhiteSpace(_options.WebhookSecret))
        {
            payload["timestamp"] = timestamp.ToString();
            payload["sign"] = FeishuCrypto.SignWebhook(timestamp, _options.WebhookSecret);
        }

        return await PostWebhookAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>给用户/会话发文本（需要自建应用权限 im:message:send）。</summary>
    public async Task<string> SendToUserAsync(
        string receiveId,
        string text,
        string receiveIdType = "open_id",
        CancellationToken cancellationToken = default)
    {
        var token = await GetTenantAccessTokenAsync(cancellationToken).ConfigureAwait(false);

        var payload = new
        {
            receive_id = receiveId,
            msg_type = "text",
            content = System.Text.Json.JsonSerializer.Serialize(new { text }),
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/open-apis/im/v1/messages?receive_id_type={receiveIdType}")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new FeishuApiException($"飞书发消息失败（HTTP {(int)response.StatusCode}）：{body}");

        var parsed = System.Text.Json.JsonSerializer.Deserialize<FeishuEnvelope>(body);

        if (parsed?.Code != 0)
            throw new FeishuApiException($"飞书发消息被拒（code={parsed?.Code}）：{parsed?.Message}");

        return parsed?.Data?.MessageId ?? "";
    }

    /// <summary>换取 tenant_access_token（带过期缓存，多实例各自缓存）。</summary>
    public async Task<string> GetTenantAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_tenantAccessToken is not null && DateTimeOffset.UtcNow < _tokenExpiresAt)
            return _tenantAccessToken;

        await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_tenantAccessToken is not null && DateTimeOffset.UtcNow < _tokenExpiresAt)
                return _tenantAccessToken;

            if (string.IsNullOrWhiteSpace(_options.AppId) || string.IsNullOrWhiteSpace(_options.AppSecret))
                throw new InvalidOperationException("自建应用发消息需要配置 AppId 与 AppSecret");

            var payload = new { app_id = _options.AppId, app_secret = _options.AppSecret };
            using var response = await _http.PostAsJsonAsync(
                "/open-apis/auth/v3/tenant_access_token/internal", payload, cancellationToken).ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var parsed = System.Text.Json.JsonSerializer.Deserialize<FeishuEnvelope>(body);

            if (parsed?.Code != 0 || string.IsNullOrWhiteSpace(parsed.TenantAccessToken))
                throw new FeishuApiException($"飞书获取 token 失败（code={parsed?.Code}）：{parsed?.Message}");

            _tenantAccessToken = parsed.TenantAccessToken;
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(
                Math.Max(60, parsed.Expire - _options.TokenRefreshSkewSeconds));

            return _tenantAccessToken;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private async Task<string> PostWebhookAsync(
        Dictionary<string, object?> payload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.WebhookUrl))
            throw new InvalidOperationException("使用自定义机器人需要配置 WebhookUrl");

        using var response = await _http.PostAsJsonAsync(_options.WebhookUrl, payload, cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new FeishuApiException($"飞书 Webhook 失败（HTTP {(int)response.StatusCode}）：{body}");

        var parsed = System.Text.Json.JsonSerializer.Deserialize<FeishuEnvelope>(body);

        if (parsed?.Code != 0)
            throw new FeishuApiException($"飞书 Webhook 被拒（code={parsed?.Code}）：{parsed?.Message}");

        return parsed?.Data?.MessageId ?? "";
    }

    public void Dispose()
    {
        _http.Dispose();
        _tokenGate.Dispose();
    }

    private sealed class FeishuEnvelope
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("msg")]
        public string? Message { get; set; }

        [JsonPropertyName("tenant_access_token")]
        public string? TenantAccessToken { get; set; }

        [JsonPropertyName("expire")]
        public int Expire { get; set; }

        [JsonPropertyName("data")]
        public FeishuData? Data { get; set; }
    }

    private sealed class FeishuData
    {
        [JsonPropertyName("message_id")]
        public string? MessageId { get; set; }
    }
}

/// <summary>飞书开放平台返回的业务错误。</summary>
public sealed class FeishuApiException(string message) : Exception(message);
