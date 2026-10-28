using Ledon.Notifications;
using Ledon.Notifications.Push;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>推送渠道配置边界：缺少路由、非法 Endpoint、FCM 缺项目、未定义网关。</summary>
public class PushEdgeTests
{
    [Fact]
    public void Adding_without_notification_router_is_rejected()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddLedonPushNotifications(options => options.Endpoint = "https://push.test"));

        Assert.Contains("AddLedonNotifications", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_webhook_endpoint_is_rejected()
    {
        var services = new ServiceCollection();
        services.AddLedonNotifications();

        Assert.Throws<InvalidOperationException>(
            () => services.AddLedonPushNotifications(options => options.Endpoint = "not-a-url"));
    }

    [Fact]
    public void Fcm_without_project_is_rejected()
    {
        var services = new ServiceCollection();
        services.AddLedonNotifications();

        Assert.Throws<InvalidOperationException>(
            () => services.AddLedonPushNotifications(options => options.Provider = PushProvider.Fcm));
    }

    [Fact]
    public void Undefined_provider_is_rejected()
    {
        var services = new ServiceCollection();
        services.AddLedonNotifications();

        Assert.Throws<InvalidOperationException>(
            () => services.AddLedonPushNotifications(options => options.Provider = (PushProvider)99));
    }
}
