using Ledon.Notifications;
using Ledon.Web;
using Ledon.Notifications.Feishu;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 飞书接入：通知渠道（发消息）+ 事件回调（收消息）。
/// <code>
/// builder.Services.AddLedonFeishu(o =&gt;
/// {
///     o.AppId = "cli_xxx";
///     o.AppSecret = "xxx";
///     o.WebhookUrl = "https://open.feishu.cn/open-apis/bot/v2/hook/xxx";
///     o.EncryptKey = "xxx";            // 事件解密
///     o.VerificationToken = "xxx";     // 回调校验
/// });
/// </code>
/// </summary>
public static class LedonFeishuServiceCollectionExtensions
{
    /// <summary>注册飞书能力（幂等）。</summary>
    public static IServiceCollection AddLedonFeishu(this IServiceCollection services)
        => services.AddLedonFeishu(_ => { });

    /// <summary>注册飞书能力并配置。</summary>
    public static IServiceCollection AddLedonFeishu(
        this IServiceCollection services,
        Action<FeishuOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new FeishuOptions();
        configure(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton(_ => new FeishuClient(options));
        services.TryAddSingleton<FeishuEventDispatcher>();

        // 事件回调端点（框架包自身不进约定扫描，端点显式注册）
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEndpoint, FeishuEventEndpoint>());

        return services;
    }

    /// <summary>把飞书接入通知路由（渠道名 <c>feishu</c>）。</summary>
    public static IServiceCollection AddLedonFeishuNotifications(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 实现 INotificationChannel 即被通知路由纳入（渠道名 feishu）
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationChannel, FeishuNotificationSender>());

        return services;
    }
}
