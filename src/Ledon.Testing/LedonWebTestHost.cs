using Ledon.Module;
using Ledon.Module.Abstractions;
using Ledon.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Testing;

/// <summary>
/// 集成测试宿主：一行搭建 TestServer + HttpClient，省掉每个测试重复的
/// CreateBuilder/UseTestServer/Build/StartAsync 样板。
/// <code>
/// await using var host = LedonWebTestHost.Create(builder =&gt; builder.AddLedonWeb());
/// var body = await host.Client.GetStringAsync("/hello");
/// </code>
/// </summary>
public sealed class LedonWebTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private HttpClient? _client;

    private LedonWebTestHost(WebApplication app)
    {
        _app = app;
    }

    /// <summary>测试客户端（TestServer，首次访问时惰性创建）。</summary>
    public HttpClient Client => _client ??= _app.GetTestClient();

    /// <summary>根服务提供器。</summary>
    public IServiceProvider Services => _app.Services;

    /// <summary>
    /// 底层 <see cref="WebApplication"/>：需要额外装配（映射端点、检查 EndpointDataSource、
    /// 读取配置）时用它，不必再手搓一套宿主。
    /// </summary>
    public WebApplication App => _app;

    /// <summary>搭建并启动宿主。</summary>
    /// <param name="configure">对 WebApplicationBuilder 的额外配置（AddLedonWeb 等）。</param>
    /// <param name="configureApp">自定义管线；不传则默认 <see cref="LedonWebApplicationExtensions.UseLedonWeb"/>。</param>
    public static Task<LedonWebTestHost> StartAsync(
        Action<WebApplicationBuilder>? configure = null,
        Action<WebApplication>? configureApp = null) =>
        StartAsync(null, configure, configureApp);

    /// <summary>搭建并启动宿主（LedonWeb 选项重载：不必在回调里再包一层）。</summary>
    /// <param name="configureWeb">LedonWeb 开关（OpenAPI/限流/压缩/超时/幂等……）。</param>
    /// <param name="configure">对 WebApplicationBuilder 的额外配置。</param>
    /// <param name="configureApp">自定义管线；不传则默认 <see cref="LedonWebApplicationExtensions.UseLedonWeb"/>。</param>
    public static async Task<LedonWebTestHost> StartAsync(
        Action<LedonWebOptions>? configureWeb,
        Action<WebApplicationBuilder>? configure = null,
        Action<WebApplication>? configureApp = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddLedonWeb(configureWeb);
        configure?.Invoke(builder);

        var app = builder.Build();

        if (configureApp is null)
            app.UseLedonWeb();
        else
            configureApp(app);

        await app.StartAsync().ConfigureAwait(false);

        return new LedonWebTestHost(app);
    }

    /// <summary>在独立作用域中执行（解析 Scoped 服务，如 DbContext）。</summary>
    public async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>模块生命周期断言助手（抛异常而非绑定具体测试框架，xunit/NUnit/MSTest 通用）。</summary>
public static class ModuleAssertions
{
    /// <summary>断言指定模块已初始化。</summary>
    public static void ShouldBeInitialized(this IModuleManager manager, string moduleName)
    {
        var descriptor = Get(manager, moduleName);
        if (descriptor.State != ModuleState.Initialized)
            throw new InvalidOperationException($"模块 {moduleName} 状态为 {descriptor.State}，期望 Initialized");
    }

    /// <summary>断言模块初始化顺序（按传入顺序，被依赖者在前）。</summary>
    public static void ShouldBeInitializedInOrder(this IModuleManager manager, params string[] moduleNames)
    {
        var actual = manager.Modules.Select(module => module.Name).ToArray();
        var positions = moduleNames.Select(name => Array.IndexOf(actual, name)).ToArray();

        foreach (var (name, position) in moduleNames.Zip(positions))
        {
            if (position < 0)
                throw new InvalidOperationException($"模块 {name} 未被发现");
        }

        var sorted = positions.OrderBy(position => position).ToArray();
        if (!positions.SequenceEqual(sorted))
            throw new InvalidOperationException(
                $"模块初始化顺序不符合预期：期望 [{string.Join(" → ", moduleNames)}]，" +
                $"实际 [{string.Join(" → ", manager.Modules.Select(m => m.Name))}]");
    }

    private static ModuleDescriptor Get(IModuleManager manager, string moduleName) =>
        manager.Modules.FirstOrDefault(module => module.Name == moduleName)
        ?? throw new InvalidOperationException($"模块 {moduleName} 未被发现");
}
