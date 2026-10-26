using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Ledon.Notifications.Sms;

/// <summary>短信渠道：把统一通知路由到具体短信网关。</summary>
public sealed class SmsNotificationChannel : INotificationChannel, IDisposable
{
    private readonly SmsOptions _options;
    private readonly ISmsGateway? _gateway;
    private readonly ILogger<SmsNotificationChannel> _logger;

    public SmsNotificationChannel(
        SmsOptions options,
        ILogger<SmsNotificationChannel>? logger = null,
        ISmsGateway? gateway = null)
    {
        _options = options;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SmsNotificationChannel>.Instance;
        _gateway = gateway ?? CreateGateway(options);
    }

    public string Channel => "sms";

    public async Task<NotificationResult> SendAsync(
        Notification notification, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(notification.Channel, "sms", StringComparison.OrdinalIgnoreCase))
            return NotificationResult.Fail($"短信发送器只处理 sms 渠道（收到 {notification.Channel}）");

        if (string.IsNullOrWhiteSpace(notification.Recipient))
            return NotificationResult.Fail("短信接收号码不能为空");

        var body = string.IsNullOrWhiteSpace(notification.Subject)
            ? notification.Body
            : $"{notification.Subject}: {notification.Body}";

        try
        {
            var result = await _gateway!.SendAsync(notification.Recipient, body, cancellationToken)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                _logger.LogWarning("短信发送失败：{Recipient} / {Error}", notification.Recipient, result.Error);
                return NotificationResult.Fail(result.Error ?? "短信网关返回失败");
            }

            _logger.LogInformation("短信已提交：{Recipient}", notification.Recipient);
            return NotificationResult.Ok(result.MessageId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "短信发送异常：{Recipient}", notification.Recipient);
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

    private static ISmsGateway CreateGateway(SmsOptions options)
    {
        if (options.Timeout <= TimeSpan.Zero)
            throw new InvalidOperationException("SmsOptions.Timeout 必须大于 0");

        var http = new HttpClient(
            options.HttpMessageHandler ?? new SocketsHttpHandler(),
            disposeHandler: options.HttpMessageHandler is null)
        {
            Timeout = options.Timeout,
        };

        return options.Provider switch
        {
            SmsProvider.Twilio => new TwilioSmsGateway(options, http),
            _ => new WebhookSmsGateway(options, http),
        };
    }
}

/// <summary>通用短信 Webhook：POST JSON <c>{ to, from, body }</c>。</summary>
public sealed class WebhookSmsGateway : ISmsGateway, IDisposable
{
    private readonly SmsOptions _options;
    private readonly HttpClient _http;

    public WebhookSmsGateway(SmsOptions options, HttpClient? http = null)
    {
        _options = options;
        _http = http ?? new HttpClient();

        if (!string.IsNullOrEmpty(options.Endpoint))
            _http.BaseAddress = new Uri(options.Endpoint);
    }

    public async Task<SmsSendResult> SendAsync(string to, string body, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Endpoint))
            return new SmsSendResult(false, Error: "未配置 SmsOptions.Endpoint");

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = JsonContent.Create(new
            {
                to,
                from = _options.From,
                body,
            }),
        };

        if (!string.IsNullOrEmpty(_options.AccessToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return new SmsSendResult(false, Error: $"HTTP {(int)response.StatusCode}: {payload}");

        return new SmsSendResult(true, TryGetMessageId(payload));
    }

    public void Dispose() => _http.Dispose();

    private static string? TryGetMessageId(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            foreach (var name in new[] { "id", "messageId", "message_id" })
            {
                if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString();
            }
        }
        catch (JsonException)
        {
            // 非 JSON 响应：只视为成功，无消息 ID
        }

        return null;
    }
}

/// <summary>Twilio Messages API：Basic Auth + form-urlencoded。</summary>
public sealed class TwilioSmsGateway : ISmsGateway, IDisposable
{
    private readonly SmsOptions _options;
    private readonly HttpClient _http;

    public TwilioSmsGateway(SmsOptions options, HttpClient? http = null)
    {
        _options = options;
        _http = http ?? new HttpClient();

        if (!string.IsNullOrEmpty(options.AccountSid) && !string.IsNullOrEmpty(options.AuthToken))
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{options.AccountSid}:{options.AuthToken}"));

            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }
    }

    public async Task<SmsSendResult> SendAsync(string to, string body, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.AccountSid) || string.IsNullOrWhiteSpace(_options.AuthToken))
            return new SmsSendResult(false, Error: "Twilio 需要配置 AccountSid/AuthToken");

        if (string.IsNullOrWhiteSpace(_options.From))
            return new SmsSendResult(false, Error: "Twilio 需要配置 From");

        var url = $"https://api.twilio.com/2010-04-01/Accounts/{Uri.EscapeDataString(_options.AccountSid)}/Messages.json";

        using var response = await _http.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["To"] = to,
            ["From"] = _options.From,
            ["Body"] = body,
        }), cancellationToken).ConfigureAwait(false);

        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return new SmsSendResult(false, Error: $"HTTP {(int)response.StatusCode}: {payload}");

        string? messageId = null;

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("sid", out var sid))
                messageId = sid.GetString();
        }
        catch (JsonException)
        {
        }

        return new SmsSendResult(true, messageId);
    }

    public void Dispose() => _http.Dispose();
}
