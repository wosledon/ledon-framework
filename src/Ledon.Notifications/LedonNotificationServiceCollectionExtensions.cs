using Ledon.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 通知入口：
/// <code>
/// builder.Services.AddLedonNotifications(o =&gt;
/// {
///     o.SmtpHost = "smtp.example.com";
///     o.FromAddress = "noreply@example.com";
/// });
/// </code>
/// 业务只依赖 <see cref="INotificationSender"/>，短信/推送网关换实现即可。
/// </summary>
public static class LedonNotificationServiceCollectionExtensions
{
    /// <summary>注册通知服务。</summary>
    public static IServiceCollection AddLedonNotifications(this IServiceCollection services)
        => services.AddLedonNotifications(_ => { });

    /// <summary>注册通知服务并配置。</summary>
    public static IServiceCollection AddLedonNotifications(
        this IServiceCollection services,
        Action<LedonNotificationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new LedonNotificationOptions();
        configure(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<INotificationLog, InMemoryNotificationLog>();

        // SMTP 渠道（内置）：实现 INotificationChannel 即被路由纳入
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationChannel, SmtpNotificationSender>());

        // 路由：按渠道名分发 + 统一重试与发送记录。
        // 渠道集合经独立的 INotificationChannel 解析——避免"在自己的工厂里解析自己"造成递归
        services.TryAddSingleton<INotificationSender, ChannelRoutingNotificationSender>();

        return services;
    }
}
