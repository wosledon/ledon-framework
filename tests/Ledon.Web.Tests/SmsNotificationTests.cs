using System.Net;
using System.Text;
using System.Text.Json;
using Ledon.Notifications;
using Ledon.Notifications.Sms;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>短信渠道：Webhook 与 Twilio 两种网关的路由、请求格式与结果映射。</summary>
public class SmsNotificationTests
{
    [Fact]
    public async Task Webhook_gateway_routes_sms_and_returns_provider_id()
    {
        var handler = new FakeSmsHandler("""{"id":"msg_1"}""");
        var sender = BuildSender(new SmsOptions
        {
            Provider = SmsProvider.Webhook,
            Endpoint = "https://sms.test/send",
            AccessToken = "token-1",
            From = "Ledon",
            HttpMessageHandler = handler,
        });

        var result = await sender.SendAsync(new Notification("sms", "+8613800000000", "Hi", "Body"));

        Assert.True(result.Accepted);
        Assert.Equal("msg_1", result.ProviderMessageId);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://sms.test/send", request.Url);
        Assert.Equal("Bearer token-1", request.Authorization);

        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("+8613800000000", body.RootElement.GetProperty("to").GetString());
        Assert.Equal("Ledon", body.RootElement.GetProperty("from").GetString());
        Assert.Equal("Hi: Body", body.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Twilio_gateway_uses_basic_auth_and_messages_api()
    {
        var handler = new FakeSmsHandler("""{"sid":"SM123"}""");
        var sender = BuildSender(new SmsOptions
        {
            Provider = SmsProvider.Twilio,
            AccountSid = "AC123",
            AuthToken = "secret",
            From = "+10000000000",
            HttpMessageHandler = handler,
        });

        var result = await sender.SendAsync(new Notification("sms", "+8613800000000", "", "Body"));

        Assert.True(result.Accepted);
        Assert.Equal("SM123", result.ProviderMessageId);

        var request = Assert.Single(handler.Requests);
        Assert.Contains("/2010-04-01/Accounts/AC123/Messages.json", request.Url);
        Assert.StartsWith("Basic ", request.Authorization);
        Assert.Contains("To=%2B8613800000000", request.Body);
        Assert.Contains("From=%2B10000000000", request.Body);
        Assert.Contains("Body=Body", request.Body);
    }

    private static INotificationSender BuildSender(SmsOptions sms)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedonNotifications(o =>
        {
            o.UseDurableQueue = false;
            o.MaxRetries = 1;
            o.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
        });

        services.AddLedonSmsNotifications(options =>
        {
            options.Provider = sms.Provider;
            options.Endpoint = sms.Endpoint;
            options.AccessToken = sms.AccessToken;
            options.From = sms.From;
            options.AccountSid = sms.AccountSid;
            options.AuthToken = sms.AuthToken;
            options.HttpMessageHandler = sms.HttpMessageHandler;
        });

        return services.BuildServiceProvider().GetRequiredService<INotificationSender>();
    }

    private sealed class FakeSmsHandler(string responseBody) : HttpMessageHandler
    {
        public List<CapturedSmsRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedSmsRequest(
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString() ?? "",
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record CapturedSmsRequest(string Url, string Authorization, string Body);
}
