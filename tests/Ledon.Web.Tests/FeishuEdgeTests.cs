using System.Net;
using System.Text;
using System.Text.Json;
using Ledon.Notifications;
using Ledon.Notifications.Feishu;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ledon.Web.Tests;

/// <summary>飞书出站边界：Webhook 签名、token 缓存、错误码与渠道过滤。</summary>
public class FeishuEdgeTests
{
    [Fact]
    public async Task Webhook_without_url_is_rejected()
    {
        using var client = new FeishuClient(new FeishuOptions());

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendTextAsync("hello"));
    }

    [Fact]
    public async Task Tenant_token_without_credentials_is_rejected()
    {
        using var client = new FeishuClient(new FeishuOptions());

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetTenantAccessTokenAsync());
    }

    [Fact]
    public async Task Tenant_token_is_cached_between_calls()
    {
        var handler = new FeishuHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://open.feishu.cn") };
        using var client = new FeishuClient(
            new FeishuOptions { AppId = "app", AppSecret = "secret" },
            http);

        var first = await client.GetTenantAccessTokenAsync();
        var second = await client.GetTenantAccessTokenAsync();

        Assert.Equal("tenant-token", first);
        Assert.Equal(first, second);
        Assert.Equal(1, handler.TokenRequests);
    }

    [Fact]
    public async Task Webhook_signature_is_added_when_secret_configured()
    {
        var handler = new FeishuHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://open.feishu.cn") };
        using var client = new FeishuClient(
            new FeishuOptions
            {
                WebhookUrl = "https://open.feishu.cn/open-apis/bot/v2/hook/test",
                WebhookSecret = "webhook-secret",
            },
            http);

        var messageId = await client.SendTextAsync("hello");

        Assert.Equal("wh_1", messageId);
        var body = JsonDocument.Parse(handler.LastWebhookBody!);
        Assert.True(body.RootElement.TryGetProperty("sign", out _));
        Assert.True(body.RootElement.TryGetProperty("timestamp", out _));
    }

    [Fact]
    public async Task Send_to_user_returns_message_id_and_uses_bearer_token()
    {
        var handler = new FeishuHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://open.feishu.cn") };
        using var client = new FeishuClient(
            new FeishuOptions { AppId = "app", AppSecret = "secret" },
            http);

        var messageId = await client.SendToUserAsync("ou_1", "hello");

        Assert.Equal("om_1", messageId);
        Assert.Equal("Bearer tenant-token", handler.LastAuthorization);
    }

    [Fact]
    public async Task Business_error_code_surfaces_as_exception()
    {
        var handler = new FeishuHandler { FailBusinessCode = true };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://open.feishu.cn") };
        using var client = new FeishuClient(
            new FeishuOptions
            {
                WebhookUrl = "https://open.feishu.cn/open-apis/bot/v2/hook/test",
            },
            http);

        await Assert.ThrowsAsync<FeishuApiException>(() => client.SendTextAsync("hello"));
    }

    [Fact]
    public async Task Sender_filters_other_channels_without_calling_the_api()
    {
        var handler = new FeishuHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://open.feishu.cn") };
        using var client = new FeishuClient(new FeishuOptions(), http);
        var sender = new FeishuNotificationSender(client, new FeishuOptions(), NullLogger<FeishuNotificationSender>.Instance);

        var result = await sender.SendAsync(new Notification("sms", "recipient", "subject", "body"));

        Assert.False(result.Accepted);
        Assert.Equal(0, handler.TotalRequests);
    }

    private sealed class FeishuHandler : HttpMessageHandler
    {
        public int TokenRequests { get; private set; }
        public int TotalRequests { get; private set; }
        public string? LastWebhookBody { get; private set; }
        public string? LastAuthorization { get; private set; }
        public bool FailBusinessCode { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            TotalRequests++;
            LastAuthorization = request.Headers.Authorization?.ToString();

            if (request.RequestUri!.AbsolutePath.Contains("tenant_access_token", StringComparison.Ordinal))
            {
                TokenRequests++;
                return Json("""{"code":0,"tenant_access_token":"tenant-token","expire":7200}""");
            }

            if (request.RequestUri.AbsolutePath.Contains("/bot/v2/hook/", StringComparison.Ordinal))
            {
                LastWebhookBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return Json(FailBusinessCode
                    ? """{"code":99,"msg":"rejected"}"""
                    : """{"code":0,"data":{"message_id":"wh_1"}}""");
            }

            return Json("""{"code":0,"data":{"message_id":"om_1"}}""");
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
    }
}
