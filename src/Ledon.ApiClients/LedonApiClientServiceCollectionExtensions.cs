using Ledon.ApiClients;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 出站 API 客户端入口：
/// <code>
/// builder.Services.AddLedonApiClient("orders", o =&gt;
/// {
///     o.BaseAddress = "https://orders.internal/";
///     o.ClientId = "...";
///     o.ClientSecret = "...";
///     o.TokenEndpoint = "https://identity/connect/token";
///     o.Scope = "orders.read";
/// });
/// </code>
/// 自动附加 Bearer 令牌、X-Correlation-ID 与 traceparent。
/// </summary>
public static class LedonApiClientServiceCollectionExtensions
{
    /// <summary>注册命名客户端（注入 IHttpClientFactory.CreateClient(name) 使用）。</summary>
    public static IHttpClientBuilder AddLedonApiClient(
        this IServiceCollection services,
        string name,
        Action<ApiClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var options = BuildOptions(configure);
        var registry = GetOrCreateRegistry(services);

        registry.Clients[name] = options;

        var builder = services.AddHttpClient(name, client => ApplyOptions(client, options));
        AddAuth(services, builder, registry, name, options);

        return builder;
    }

    /// <summary>
    /// 注册类型化客户端：<typeparamref name="TClient"/> 构造函数接收 <see cref="HttpClient"/>，
    /// 可注入到业务服务中直接调用。
    /// </summary>
    public static IHttpClientBuilder AddLedonApiClient<TClient>(
        this IServiceCollection services,
        string name,
        Action<ApiClientOptions>? configure = null)
        where TClient : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var options = BuildOptions(configure);
        var registry = GetOrCreateRegistry(services);

        registry.Clients[name] = options;

        var builder = services.AddHttpClient<TClient>(name, client => ApplyOptions(client, options));
        AddAuth(services, builder, registry, name, options);

        return builder;
    }

    private static ApiClientOptions BuildOptions(Action<ApiClientOptions>? configure)
    {
        var options = new ApiClientOptions();
        configure?.Invoke(options);

        if (options.BaseAddress is not null && !Uri.TryCreate(options.BaseAddress, UriKind.Absolute, out _))
            throw new InvalidOperationException("ApiClientOptions.BaseAddress 必须是绝对地址");

        if (options.TokenEndpoint is not null && !Uri.TryCreate(options.TokenEndpoint, UriKind.Absolute, out _))
            throw new InvalidOperationException("ApiClientOptions.TokenEndpoint 必须是绝对地址");

        var hasClientId = !string.IsNullOrWhiteSpace(options.ClientId);
        var hasSecret = !string.IsNullOrWhiteSpace(options.ClientSecret);

        if (hasClientId != hasSecret)
            throw new InvalidOperationException("ApiClientOptions.ClientId 与 ClientSecret 必须同时配置");

        if ((hasClientId || hasSecret) && string.IsNullOrWhiteSpace(options.TokenEndpoint))
            throw new InvalidOperationException("配置客户端凭据时必须同时配置 TokenEndpoint");

        if (options.Timeout <= TimeSpan.Zero)
            throw new InvalidOperationException("ApiClientOptions.Timeout 必须大于 0");

        return options;
    }

    private static void ApplyOptions(HttpClient client, ApiClientOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.BaseAddress))
            client.BaseAddress = new Uri(options.BaseAddress, UriKind.Absolute);

        client.Timeout = options.Timeout;

        foreach (var (name, value) in options.DefaultHeaders)
            client.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
    }

    private static void AddAuth(
        IServiceCollection services,
        IHttpClientBuilder builder,
        ApiClientRegistry registry,
        string name,
        ApiClientOptions options)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();

        builder.AddHttpMessageHandler(provider =>
        {
            var tokenProvider = registry.Providers.GetOrAdd(name, _ => new ClientCredentialsTokenProvider(
                options,
                provider.GetRequiredService<IHttpClientFactory>(),
                provider.GetRequiredService<TimeProvider>()));

            return new ApiClientAuthHandler(
                tokenProvider,
                options,
                provider.GetService<IHttpContextAccessor>());
        });
    }

    private static ApiClientRegistry GetOrCreateRegistry(IServiceCollection services)
    {
        var descriptor = services.FirstOrDefault(service => service.ServiceType == typeof(ApiClientRegistry));

        if (descriptor?.ImplementationInstance is ApiClientRegistry existing)
            return existing;

        var registry = new ApiClientRegistry();
        services.AddSingleton(registry);

        return registry;
    }
}
