using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Ledon.Notifications.Push;

/// <summary>推送渠道：把统一通知路由到具体推送网关。</summary>
public sealed class PushNotificationChannel : INotificationChannel, IDisposable
{
    private readonly IPushGateway? _gateway;
    private readonly ILogger<PushNotificationChannel> _logger;

    public PushNotificationChannel(
        PushOptions options,
        ILogger<PushNotificationChannel>? logger = null,
        IPushGateway? gateway = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PushNotificationChannel>.Instance;
        _gateway = gateway ?? CreateGateway(options);
    }

    public string Channel => "push";

    public async Task<NotificationResult> SendAsync(
        Notification notification, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(notification.Channel, "push", StringComparison.OrdinalIgnoreCase))
            return NotificationResult.Fail($"推送发送器只处理 push 渠道（收到 {notification.Channel}）");

        if (string.IsNullOrWhiteSpace(notification.Recipient))
            return NotificationResult.Fail("推送设备标识不能为空");

        var data = notification.Metadata is { Count: > 0 }
            ? new Dictionary<string, string>(notification.Metadata, StringComparer.Ordinal)
            : null;

        if (!string.IsNullOrEmpty(notification.TemplateName))
            (data ??= [])["template"] = notification.TemplateName;

        try
        {
            var result = await _gateway!.SendAsync(
                notification.Recipient,
                notification.Subject,
                notification.Body,
                data,
                cancellationToken).ConfigureAwait(false);

            if (!result.Success)
            {
                _logger.LogWarning("推送失败");
                return NotificationResult.Fail(result.Error ?? "推送网关返回失败");
            }

            _logger.LogInformation("推送已提交");
            return NotificationResult.Ok(result.MessageId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "推送发送异常");
            return NotificationResult.Fail(ex.Message);
        }
    }

    public async Task<IReadOnlyList<NotificationResult>> SendAsync(
        IReadOnlyCollection<Notification> notifications, CancellationToken cancellationToken = default)
    {
        var results = new List<NotificationResult>(notifications.Count);

        foreach (var notification in notifications)
            results.Add(await SendAsync(notification, cancellationToken).ConfigureAwait(false));

        return results;
    }

    public void Dispose() => (_gateway as IDisposable)?.Dispose();

    private static IPushGateway CreateGateway(PushOptions options)
    {
        if (options.Timeout <= TimeSpan.Zero)
            throw new InvalidOperationException("PushOptions.Timeout 必须大于 0");

        var http = new HttpClient(
            options.HttpMessageHandler ?? new SocketsHttpHandler(),
            disposeHandler: options.HttpMessageHandler is null)
        {
            Timeout = options.Timeout,
        };

        return options.Provider switch
        {
            PushProvider.Fcm => new FcmPushGateway(options, http),
            _ => new WebhookPushGateway(options, http),
        };
    }
}

/// <summary>通用推送 Webhook：POST JSON <c>{ token, title, body, data }</c>。</summary>
public sealed class WebhookPushGateway : IPushGateway, IDisposable
{
    private readonly PushOptions _options;
    private readonly HttpClient _http;

    public WebhookPushGateway(PushOptions options, HttpClient? http = null)
    {
        _options = options;
        _http = http ?? new HttpClient();
    }

    public async Task<PushSendResult> SendAsync(
        string deviceToken,
        string title,
        string body,
        IReadOnlyDictionary<string, string>? data = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Endpoint))
            return new PushSendResult(false, Error: "未配置 PushOptions.Endpoint");

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = JsonContent.Create(new { token = deviceToken, title, body, data }),
        };

        if (!string.IsNullOrEmpty(_options.AccessToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return response.IsSuccessStatusCode
            ? new PushSendResult(true, TryGetMessageId(payload))
            : new PushSendResult(false, Error: $"HTTP {(int)response.StatusCode}: {payload}");
    }

    public void Dispose() => _http.Dispose();

    private static string? TryGetMessageId(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            foreach (var name in new[] { "id", "messageId", "message_id", "name" })
            {
                if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }
}

/// <summary>Firebase Cloud Messaging HTTP v1：Bearer OAuth2 + messages:send。</summary>
public sealed class FcmPushGateway : IPushGateway, IDisposable
{
    private readonly PushOptions _options;
    private readonly HttpClient _http;

    public FcmPushGateway(PushOptions options, HttpClient? http = null)
    {
        _options = options;
        _http = http ?? new HttpClient();
    }

    public async Task<PushSendResult> SendAsync(
        string deviceToken,
        string title,
        string body,
        IReadOnlyDictionary<string, string>? data = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ProjectId))
            return new PushSendResult(false, Error: "FCM 需要配置 ProjectId");

        var accessToken = _options.AccessTokenProvider is not null
            ? await _options.AccessTokenProvider(cancellationToken).ConfigureAwait(false)
            : _options.AccessToken;

        if (string.IsNullOrWhiteSpace(accessToken))
            return new PushSendResult(false, Error: "FCM 需要配置 AccessToken 或 AccessTokenProvider");

        var url = $"https://fcm.googleapis.com/v1/projects/{Uri.EscapeDataString(_options.ProjectId)}/messages:send";

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new
            {
                message = new
                {
                    token = deviceToken,
                    notification = new { title, body },
                    data,
                },
            }),
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return new PushSendResult(false, Error: $"HTTP {(int)response.StatusCode}: {payload}");

        return new PushSendResult(true, TryGetName(payload));
    }

    public void Dispose() => _http.Dispose();

    private static string? TryGetName(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty("name", out var name) ? name.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
