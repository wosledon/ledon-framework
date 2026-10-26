using Ledon.Notifications;
using Ledon.Notifications.Push;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 推送通知渠道入口（需先调用 <c>AddLedonNotifications()</c>）：
/// <code>
/// builder.Services.AddLedonNotifications();
/// builder.Services.AddLedonPushNotifications(o =&gt;
/// {
///     o.Provider = PushProvider.Fcm;
///     o.ProjectId = "my-project";
///     o.AccessTokenProvider = ct =&gt; GetGoogleAccessTokenAsync(ct);
/// });
/// </code>
/// </summary>
public static class LedonPushNotificationServiceCollectionExtensions
{
    public static IServiceCollection AddLedonPushNotifications(
        this IServiceCollection services,
        Action<PushOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        if (!services.Any(descriptor => descriptor.ServiceType == typeof(INotificationSender)))
            throw new InvalidOperationException("请先调用 AddLedonNotifications() 注册渠道路由");

        var options = new PushOptions();
        configure(options);

        if (!Enum.IsDefined(options.Provider))
            throw new InvalidOperationException($"未定义的推送网关类型：{options.Provider}");

        if (options.Timeout <= TimeSpan.Zero)
            throw new InvalidOperationException("PushOptions.Timeout 必须大于 0");

        switch (options.Provider)
        {
            case PushProvider.Webhook when string.IsNullOrWhiteSpace(options.Endpoint)
                || !Uri.TryCreate(options.Endpoint, UriKind.Absolute, out _):
                throw new InvalidOperationException("Webhook 推送网关需要配置合法的 Endpoint");

            case PushProvider.Fcm when string.IsNullOrWhiteSpace(options.ProjectId):
                throw new InvalidOperationException("FCM 推送网关需要配置 ProjectId");
        }

        services.TryAddSingleton(options);

        services.TryAddSingleton<IPushGateway>(_ =>
        {
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
        });

        services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationChannel, PushNotificationChannel>());

        return services;
    }
}
