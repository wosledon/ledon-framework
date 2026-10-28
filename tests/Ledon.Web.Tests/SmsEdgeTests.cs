using Ledon.Notifications;
using Ledon.Notifications.Sms;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>短信渠道配置边界：缺少路由、非法 Endpoint、Twilio 缺凭据、未定义网关。</summary>
public class SmsEdgeTests
{
    [Fact]
    public void Adding_without_notification_router_is_rejected()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddLedonSmsNotifications(options => options.Endpoint = "https://sms.test"));

        Assert.Contains("AddLedonNotifications", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_webhook_endpoint_is_rejected()
    {
        var services = new ServiceCollection();
        services.AddLedonNotifications();

        Assert.Throws<InvalidOperationException>(
            () => services.AddLedonSmsNotifications(options => options.Endpoint = "not-a-url"));
    }

    [Fact]
    public void Twilio_without_credentials_is_rejected()
    {
        var services = new ServiceCollection();
        services.AddLedonNotifications();

        Assert.Throws<InvalidOperationException>(
            () => services.AddLedonSmsNotifications(options => options.Provider = SmsProvider.Twilio));
    }

    [Fact]
    public void Undefined_provider_is_rejected()
    {
        var services = new ServiceCollection();
        services.AddLedonNotifications();

        Assert.Throws<InvalidOperationException>(
            () => services.AddLedonSmsNotifications(options => options.Provider = (SmsProvider)99));
    }
}
