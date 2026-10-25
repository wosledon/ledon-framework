using Ledon.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

        // 渠道实现按渠道名分发：默认 SMTP；短信/推送可再注册 INotificationSender
        services.TryAddSingleton<INotificationSender>(provider =>
        {
            var resolved = provider.GetRequiredService<LedonNotificationOptions>();
            var loggerFactory = provider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;

            return new ChannelRoutingNotificationSender(
                new SmtpNotificationSender(resolved, loggerFactory.CreateLogger<SmtpNotificationSender>()),
                resolved,
                provider.GetService<INotificationLog>() ?? new InMemoryNotificationLog(),
                loggerFactory.CreateLogger<ChannelRoutingNotificationSender>());
        });

        return services;
    }
}
