using Ledon;
using Ledon.Application;
using Ledon.Module;
using Ledon.Module.Abstractions;
using Ledon.Caching;
using Ledon.Transactions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 应用层入口（CQRS + 管线）：
/// <code>
/// builder.Services.AddLedonApplication(o =&gt;
/// {
///     o.AddBehavior(BehaviorRegistration.All&lt;ValidationBehavior&lt;IValidatable&gt;&gt;());
///     o.AddBehavior(BehaviorRegistration.All&lt;LoggingBehavior&lt;IValidatable&gt;&gt;());
///     o.AddBehavior(BehaviorRegistration.All&lt;TransactionBehavior&lt;IValidatable&gt;&gt;());
/// });
///
/// var id = await sender.SendAsync(new CreateOrder(cmd), ct);
/// </code>
/// 处理器经约定扫描自动注册（与 <c>IAccountService</c> 同名接口规则一致：
/// 处理器不需要额外接口约定，靠 <c>ICommandHandler&lt;,&gt;</c> 识别）。
/// </summary>
public static class LedonApplicationServiceCollectionExtensions
{
    /// <summary>注册应用层服务。</summary>
    public static IServiceCollection AddLedonApplication(this IServiceCollection services)
        => services.AddLedonApplication(_ => { });

    /// <summary>注册应用层服务并配置管线。</summary>
    public static IServiceCollection AddLedonApplication(
        this IServiceCollection services,
        Action<LedonApplicationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new LedonApplicationOptions();
        configure(options);

        services.TryAddSingleton(options);

        // 请求发送者必须是 Scoped：它用注入的 IServiceProvider 解析处理器，
        // 而处理器按 Scoped 注册（可能持有 DbContext/UoW）。注册成 Singleton 时
        // 开发环境（ValidateScopes）一请求就炸，生产环境则让所有请求共享同一个
        // Scoped 实例（DbContext 跨请求复用 = 线程安全问题 + 状态泄漏）。
        services.TryAddScoped<IRequestSender, RequestSender>();

        // 处理器自动注册：扫描业务程序集，把 ICommandHandler<,>/IQueryHandler<,> 实现按接口注册
        foreach (var assembly in LedonAssemblies.Scan())
        {
            foreach (var type in assembly.GetLoadableTypes()
                .Where(candidate => candidate is { IsClass: true, IsAbstract: false }
                    && !candidate.IsDefined(typeof(DisableAutoDiscoveryAttribute), inherit: false)))
            {
                foreach (var handlerInterface in type.GetInterfaces()
                    .Where(candidate => candidate.IsGenericType
                        && (candidate.GetGenericTypeDefinition() == typeof(ICommandHandler<,>)
                            || candidate.GetGenericTypeDefinition() == typeof(IQueryHandler<,>)
                            || candidate == typeof(ICommandHandler<>)))
                    .Distinct())
                {
                    services.TryAdd(new ServiceDescriptor(handlerInterface, type, ServiceLifetime.Scoped));
                }
            }
        }

        // 内置行为与请求发送者同生命周期：同样要在请求作用域内解析，
        // 否则注入的 IServiceProvider 会是根容器（解析不到 Scoped 的 UoW/缓存）
        services.TryAddScoped(typeof(ValidationBehavior<>));
        services.TryAddScoped(typeof(LoggingBehavior<>));
        services.TryAddScoped(typeof(TransactionBehavior<>));
        services.TryAddScoped(typeof(CachingBehavior<>));

        // 登记的行为自动进容器（调用方不必手动注册）
        foreach (var registration in options.Behaviors)
            services.TryAdd(
                new ServiceDescriptor(registration.BehaviorType, registration.BehaviorType, ServiceLifetime.Scoped));

        return services;
    }
}
