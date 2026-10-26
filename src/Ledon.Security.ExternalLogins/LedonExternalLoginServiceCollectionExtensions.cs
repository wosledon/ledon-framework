using Ledon.Security;
using Ledon.Security.ExternalLogins;
using Ledon.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 外部登录入口（需先 <c>AddLedonSecurity()</c>）：
/// <code>
/// builder.Services.AddLedonSecurity(o =&gt; o.Jwt.SigningKey = "...");
/// builder.Services.AddLedonExternalLogins(o =&gt;
/// {
///     o.Providers.Add(new ExternalLoginProviderOptions
///     {
///         Name = "github",
///         Type = ExternalLoginProviderType.GitHub,
///         ClientId = "...",
///         ClientSecret = "...",
///     });
/// });
/// </code>
/// challenge：<c>GET /auth/external/{provider}/challenge</c>；
/// callback：<c>GET /auth/external/{provider}/callback</c>，成功返回框架双令牌。
/// </summary>
public static class LedonExternalLoginServiceCollectionExtensions
{
    public static IServiceCollection AddLedonExternalLogins(
        this IServiceCollection services,
        Action<ExternalLoginOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        if (!services.Any(descriptor => descriptor.ServiceType == typeof(IJwtTokenGenerator)))
            throw new InvalidOperationException("请先调用 AddLedonSecurity() 注册令牌签发服务");

        var options = new ExternalLoginOptions();
        configure(options);

        if (options.Providers.Count == 0)
            throw new InvalidOperationException("至少配置一个外部登录提供程序");

        if (options.StateLifetime <= TimeSpan.Zero)
            throw new InvalidOperationException("ExternalLoginOptions.StateLifetime 必须大于 0");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in options.Providers)
        {
            if (string.IsNullOrWhiteSpace(provider.Name))
                throw new InvalidOperationException("外部登录提供程序缺少 Name");

            if (!names.Add(provider.Name))
                throw new InvalidOperationException($"外部登录提供程序 Name 重复：{provider.Name}");

            if (string.IsNullOrWhiteSpace(provider.ClientId) || string.IsNullOrWhiteSpace(provider.ClientSecret))
                throw new InvalidOperationException($"外部登录提供程序 {provider.Name} 缺少 ClientId/ClientSecret");

            if (provider.Type == ExternalLoginProviderType.Oidc
                && (string.IsNullOrWhiteSpace(provider.AuthorizationEndpoint)
                    || string.IsNullOrWhiteSpace(provider.TokenEndpoint)
                    || string.IsNullOrWhiteSpace(provider.UserInfoEndpoint)))
            {
                throw new InvalidOperationException(
                    $"OIDC 提供程序 {provider.Name} 需要配置 AuthorizationEndpoint/TokenEndpoint/UserInfoEndpoint");
            }
        }

        services.AddDataProtection();
        services.TryAddSingleton(options);
        services.TryAddSingleton<ExternalLoginStateProtector>();
        services.TryAddSingleton<IExternalUserResolver, DefaultExternalUserResolver>();

        foreach (var provider in options.Providers)
        {
            var definition = provider;

            services.AddSingleton<IExternalLoginProvider>(_ =>
                definition.Type == ExternalLoginProviderType.GitHub
                    ? new GitHubExternalLoginProvider(definition, options.HttpMessageHandler)
                    : new OidcExternalLoginProvider(definition, options.HttpMessageHandler));
        }

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEndpoint, ExternalLoginEndpoints>());

        return services;
    }
}
