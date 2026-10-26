using Ledon.Notifications;
using Ledon.Notifications.Sms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 短信通知渠道入口（需先调用 <c>AddLedonNotifications()</c>）：
/// <code>
/// builder.Services.AddLedonNotifications();
/// builder.Services.AddLedonSmsNotifications(o =&gt;
/// {
///     o.Provider = SmsProvider.Webhook;
///     o.Endpoint = "https://sms-gateway.example.com/send";
///     o.AccessToken = "...";
/// });
/// </code>
/// </summary>
public static class LedonSmsNotificationServiceCollectionExtensions
{
    public static IServiceCollection AddLedonSmsNotifications(
        this IServiceCollection services,
        Action<SmsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        if (!services.Any(descriptor => descriptor.ServiceType == typeof(INotificationSender)))
            throw new InvalidOperationException("请先调用 AddLedonNotifications() 注册渠道路由");

        var options = new SmsOptions();
        configure(options);

        if (!Enum.IsDefined(options.Provider))
            throw new InvalidOperationException($"未定义的短信网关类型：{options.Provider}");

        if (options.Timeout <= TimeSpan.Zero)
            throw new InvalidOperationException("SmsOptions.Timeout 必须大于 0");

        switch (options.Provider)
        {
            case SmsProvider.Webhook when string.IsNullOrWhiteSpace(options.Endpoint)
                || !Uri.TryCreate(options.Endpoint, UriKind.Absolute, out _):
                throw new InvalidOperationException("Webhook 短信网关需要配置合法的 Endpoint");

            case SmsProvider.Twilio when string.IsNullOrWhiteSpace(options.AccountSid)
                || string.IsNullOrWhiteSpace(options.AuthToken)
                || string.IsNullOrWhiteSpace(options.From):
                throw new InvalidOperationException("Twilio 短信网关需要配置 AccountSid/AuthToken/From");
        }

        services.TryAddSingleton(options);

        services.TryAddSingleton<ISmsGateway>(_ =>
        {
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
        });

        services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationChannel, SmsNotificationChannel>());

        return services;
    }
}
