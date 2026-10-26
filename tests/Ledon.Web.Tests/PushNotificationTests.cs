using System.Net;
using System.Text;
using System.Text.Json;
using Ledon.Notifications;
using Ledon.Notifications.Push;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>推送渠道：Webhook 与 FCM HTTP v1 的路由、请求格式与结果映射。</summary>
public class PushNotificationTests
{
    [Fact]
    public async Task Webhook_gateway_routes_push_and_passes_metadata()
    {
        var handler = new FakePushHandler("""{"id":"push_1"}""");
        var sender = BuildSender(new PushOptions
        {
            Provider = PushProvider.Webhook,
            Endpoint = "https://push.test/send",
            AccessToken = "token-1",
            HttpMessageHandler = handler,
        });

        var result = await sender.SendAsync(new Notification(
            "push",
            "device-token",
            "标题",
            "正文",
            TemplateName: "welcome",
            Metadata: new Dictionary<string, string> { ["orderId"] = "42" }));

        Assert.True(result.Accepted);
        Assert.Equal("push_1", result.ProviderMessageId);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://push.test/send", request.Url);
        Assert.Equal("Bearer token-1", request.Authorization);

        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("device-token", body.RootElement.GetProperty("token").GetString());
        Assert.Equal("标题", body.RootElement.GetProperty("title").GetString());
        Assert.Equal("正文", body.RootElement.GetProperty("body").GetString());
        Assert.Equal("42", body.RootElement.GetProperty("data").GetProperty("orderId").GetString());
        Assert.Equal("welcome", body.RootElement.GetProperty("data").GetProperty("template").GetString());
    }

    [Fact]
    public async Task Fcm_gateway_uses_v1_endpoint_and_bearer_token()
    {
        var handler = new FakePushHandler("""{"name":"projects/my-project/messages/123"}""");
        var sender = BuildSender(new PushOptions
        {
            Provider = PushProvider.Fcm,
            ProjectId = "my-project",
            AccessToken = "google-token",
            HttpMessageHandler = handler,
        });

        var result = await sender.SendAsync(new Notification("push", "device-token", "Title", "Body"));

        Assert.True(result.Accepted);
        Assert.Equal("projects/my-project/messages/123", result.ProviderMessageId);

        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/v1/projects/my-project/messages:send", request.Url);
        Assert.Equal("Bearer google-token", request.Authorization);

        using var body = JsonDocument.Parse(request.Body);
        var message = body.RootElement.GetProperty("message");
        Assert.Equal("device-token", message.GetProperty("token").GetString());
        Assert.Equal("Title", message.GetProperty("notification").GetProperty("title").GetString());
        Assert.Equal("Body", message.GetProperty("notification").GetProperty("body").GetString());
    }

    private static INotificationSender BuildSender(PushOptions push)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedonNotifications(o =>
        {
            o.UseDurableQueue = false;
            o.MaxRetries = 1;
            o.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
        });

        services.AddLedonPushNotifications(options =>
        {
            options.Provider = push.Provider;
            options.Endpoint = push.Endpoint;
            options.AccessToken = push.AccessToken;
            options.ProjectId = push.ProjectId;
            options.HttpMessageHandler = push.HttpMessageHandler;
        });

        return services.BuildServiceProvider().GetRequiredService<INotificationSender>();
    }

    private sealed class FakePushHandler(string responseBody) : HttpMessageHandler
    {
        public List<CapturedPushRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedPushRequest(
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString() ?? "",
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record CapturedPushRequest(string Url, string Authorization, string Body);
}
