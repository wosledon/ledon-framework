using System.Text;
using Ledon;
using Ledon.Module;
using Ledon.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 认证授权入口。日常用法一行：
/// <code>
/// builder.Services.AddLedonSecurity(o =&gt; o.Jwt.SigningKey = Configuration["Jwt:Key"]!);
/// </code>
/// 之后：<c>[Authorize]</c> 走 JWT Bearer；<c>[Authorize(Policy = "权限名")]</c> 走权限策略；
/// 配置了 API Key 时请求头 <c>X-Api-Key</c> 亦可认证。刷新令牌签发/轮换走
/// <see cref="IRefreshTokenService"/>（自动注册，默认进程内存储）。
/// </summary>
public static class LedonSecurityServiceCollectionExtensions
{
    public static IServiceCollection AddLedonSecurity(
        this IServiceCollection services,
        Action<LedonSecurityOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new LedonSecurityOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton<IJwtTokenGenerator, JwtTokenGenerator>();
        services.AddHttpContextAccessor();
        services.AddSingleton<ICurrentUser, CurrentUser>();

        // 刷新令牌（TryAdd：多实例部署可替换为共享存储实现后自行注册）
        services.TryAddSingleton<IRefreshTokenStore, InMemoryRefreshTokenStore>();
        services.TryAddSingleton<IRefreshTokenService, RefreshTokenService>();

        // 登录校验实现自动发现（实现 ILedonUserValidator 即被 /auth/login 端点使用；手动注册优先）
        foreach (var assembly in LedonAssemblies.Scan())
        {
            foreach (var type in assembly.GetLoadableTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false }
                    && typeof(ILedonUserValidator).IsAssignableFrom(type)))
            {
                services.TryAdd(ServiceDescriptor.Scoped(typeof(ILedonUserValidator), type));
            }
        }

        var authentication = services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme);

        // JWT Bearer（默认方案）
        authentication.AddJwtBearer(jwt =>
        {
            jwt.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = options.Jwt.Issuer,
                ValidAudience = options.Jwt.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Jwt.SigningKey)),
            };
        });

        // API Key（配置了 Key 才注册）
        if (options.ApiKey.Keys.Count > 0)
        {
            authentication.AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationHandler.SchemeName, null);
        }

        // 权限策略（策略名 = 权限名）
        services.AddAuthorization(authorization =>
        {
            // 配置了 API Key 时，默认策略接受 JWT 或 API Key 任一方案——
            // [Authorize] 无需关心客户端用哪种方式认证
            if (options.ApiKey.Keys.Count > 0)
            {
                authorization.DefaultPolicy = new AuthorizationPolicyBuilder(
                        JwtBearerDefaults.AuthenticationScheme,
                        ApiKeyAuthenticationHandler.SchemeName)
                    .RequireAuthenticatedUser()
                    .Build();
            }
        });

        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();

        return services;
    }
}
