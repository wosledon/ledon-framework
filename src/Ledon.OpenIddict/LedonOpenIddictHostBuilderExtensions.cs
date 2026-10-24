using System.Diagnostics.CodeAnalysis;
using Ledon;
using Ledon.Module;
using Ledon.OpenIddict;
using Ledon.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Validation;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// OpenIddict OIDC 服务器入口：标准 /connect/token（password / refresh_token / client_credentials），
/// JWT 访问令牌，内存存储（配置驱动客户端 + 刷新流程），与框架的 ILedonUserValidator 组合。
/// <code>
/// builder.AddLedonOpenIddict(o =&gt;
/// {
///     o.SigningKey = Configuration["Jwt:Key"]!;
///     o.Clients.Add(new OpenIddictClientDefinition
///     {
///         ClientId = "webapp",
///         GrantTypes = ["password", "refresh_token"],
///     });
/// });
/// app.UseAuthentication();
/// </code>
/// 多实例部署：把四个内存存储换成 OpenIddict 官方的 EF/Redis 存储（本包的
/// AddLedonOpenIddictServices 只注册默认实现，可先行替换）。
/// </summary>
public static class LedonOpenIddictHostBuilderExtensions
{
    /// <summary>反射边界说明（裁剪/NativeAOT 警告文案）。</summary>
    private const string AotBoundary =
        "Ledon 的登录校验自动发现基于反射（Assembly.GetTypes）。" +
        "NativeAOT 或裁剪发布时，相关类型必须可在运行时解析（rd.xml / DynamicDependency 保留）。";

    [RequiresDynamicCode(AotBoundary)]
    public static WebApplicationBuilder AddLedonOpenIddict(
        this WebApplicationBuilder builder,
        Action<LedonOpenIddictOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new LedonOpenIddictOptions();
        configure?.Invoke(options);

        if (options.Clients.Count == 0)
            throw new InvalidOperationException("AddLedonOpenIddict: 至少配置一个客户端（o.Clients）");

        builder.Services.AddLedonOpenIddictServices(options);

        return builder;
    }

    /// <summary>服务注册部分（供非 Web 宿主/高级装配直接使用）。</summary>
    public static IServiceCollection AddLedonOpenIddictServices(
        this IServiceCollection services,
        LedonOpenIddictOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(options);

        // 处理器依赖的权限 claim 类型（未配 AddLedonSecurity 时用默认值；配了则复用其配置）
        services.TryAddSingleton(new LedonSecurityOptions());

        // 默认 scope（未配置时）
        if (options.Scopes.Count == 0)
        {
            options.Scopes.Add(new OpenIddictScopeDefinition { Name = "openid", DisplayName = "身份标识" });
            options.Scopes.Add(new OpenIddictScopeDefinition { Name = "profile", DisplayName = "用户资料" });
        }

        var openIddict = services.AddOpenIddict();

        // 核心：管理器/存储基础设施（服务端与校验端都依赖）；默认实体绑定到内存存储用的 Descriptor
        var core = openIddict.AddCore();
        core.SetDefaultApplicationEntity<OpenIddictApplicationDescriptor>();
        core.SetDefaultAuthorizationEntity<OpenIddictAuthorizationDescriptor>();
        core.SetDefaultScopeEntity<OpenIddictScopeDescriptor>();
        core.SetDefaultTokenEntity<OpenIddictTokenDescriptor>();

        // 服务端：token 端点 + 三个流程 + JWT 访问令牌
        var server = openIddict.AddServer();
        server
            .SetTokenEndpointUris("/connect/token")
            .SetIssuer(new Uri(options.Issuer))
            .AllowPasswordFlow()
            .AllowRefreshTokenFlow()
            .AllowClientCredentialsFlow()
            .SetAccessTokenLifetime(options.AccessTokenLifetime)
            .SetRefreshTokenLifetime(options.RefreshTokenLifetime)

            // Data Protection 令牌格式要求非对称密钥：默认进程内临时 RSA（单实例开箱即用）。
            // 多实例部署换成共享密钥（证书/导出的 RSA 参数，OpenIddict 的 AddSigningKey 重载）——见 README。
            .AddEphemeralSigningKey()
            .AddEphemeralEncryptionKey()

            // 访问令牌用纯 JWS（不加密）：校验端只需签名密钥，避免 JWE 解密依赖
            .DisableAccessTokenEncryption();

        // 令牌保护用宿主的 Data Protection（配了 Redis 码环则多实例共享）；
        // 访问令牌优先标准 JWT 格式（JWS）——校验端/第三方库无需 DP 即可验
        server.UseDataProtection()
            .PreferDefaultAccessTokenFormat();

        if (options.UseUserInfoEndpoint)
            server.SetUserInfoEndpointUris("/connect/userinfo");

        // 凭证校验与身份构建：复用框架的 ILedonUserValidator
        server.AddEventHandler<OpenIddict.Server.OpenIddictServerEvents.HandleTokenRequestContext>(
            handlerBuilder => handlerBuilder.UseSingletonHandler<ResourceOwnerCredentialsHandler>());

        // ASP.NET Core 承载 + 允许 http 开发端点（生产应配 HTTPS）
        server.UseAspNetCore()
            .DisableTransportSecurityRequirement();

        // 校验端：让本服务的 [Authorize] 也能验本服务签发的令牌（资源服务器场景）
        var validation = openIddict.AddValidation();
        validation
            .SetIssuer(options.Issuer);

        // 本地服务器签发的令牌 + ASP.NET Core 承载（[Authorize] 集成）
        validation.UseLocalServer();
        validation.UseAspNetCore();

        // [Authorize] 默认走 OpenIddict 校验方案（应用另配了默认方案时不覆盖——后配置者胜出）
        services.Configure<Microsoft.AspNetCore.Authentication.AuthenticationOptions>(authentication =>
        {
            var scheme = OpenIddict.Validation.AspNetCore.OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;

            authentication.DefaultScheme ??= scheme;
            authentication.DefaultChallengeScheme ??= authentication.DefaultScheme;
            authentication.DefaultAuthenticateScheme ??= authentication.DefaultScheme;
            authentication.DefaultForbidScheme ??= authentication.DefaultScheme;
        });

        // 四个默认存储（可替换：先注册自己的实现即生效——TryAdd 语义，手动优先。
        // 多实例部署换 OpenIddict 官方的 EF/Redis 存储时在此前注册即可：
        //   services.AddSingleton<IOpenIddictTokenStore<OpenIddictTokenDescriptor>, MyRedisTokenStore>();）
        services.TryAdd(ServiceDescriptor.Singleton(
            typeof(IOpenIddictApplicationStore<OpenIddictApplicationDescriptor>),
            new InMemoryApplicationStore(options)));
        services.TryAdd(ServiceDescriptor.Singleton(
            typeof(IOpenIddictScopeStore<OpenIddictScopeDescriptor>),
            new InMemoryScopeStore(options)));
        services.TryAdd(ServiceDescriptor.Singleton(
            typeof(IOpenIddictAuthorizationStore<OpenIddictAuthorizationDescriptor>),
            new InMemoryAuthorizationStore()));
        services.TryAdd(ServiceDescriptor.Singleton(
            typeof(IOpenIddictTokenStore<OpenIddictTokenDescriptor>),
            new InMemoryTokenStore()));

        // 登录校验实现自动发现（password 授权的凭证校验；与 AddLedonSecurity 同一约定，手动注册优先）
        foreach (var assembly in LedonAssemblies.Scan())
        {
            foreach (var type in assembly.GetLoadableTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false }
                    && typeof(ILedonUserValidator).IsAssignableFrom(type)))
            {
                services.TryAdd(ServiceDescriptor.Scoped(typeof(ILedonUserValidator), type));
            }
        }

        // 客户端初始化（宿主启动时经管理器创建，密钥走 OpenIddict 哈希约定）
        services.AddHostedService<OpenIddictClientInitializer>();

        return services;
    }
}
