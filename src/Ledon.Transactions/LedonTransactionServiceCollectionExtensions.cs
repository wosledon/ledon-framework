using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Ledon.Data;
using Ledon.Module;
using Ledon.Module.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ledon.Transactions;

/// <summary>
/// 候选 DbContext 类型注册表（启动时填充，供 <see cref="EfUnitOfWork"/> 解析主上下文）。
/// </summary>
internal static class UnitOfWorkRegistry
{
    public static IReadOnlyList<Type> CandidateContextTypes { get; private set; } = [];

    public static void Set(IEnumerable<Type> types) => CandidateContextTypes = types.ToArray();
}

/// <summary>
/// 统一事务入口扩展。日常用法一行：
/// <code>
/// builder.Services.AddLedonTransactions();
///
/// // 业务代码（本地/分布式同一种写法）
/// await uow.ExecuteAsync(async ct =>
/// {
///     uow.AddIntegrationEvent(new OrderPlaced(order.Id));
///     await uow.SaveChangesAsync(ct);
/// }, ct);
/// </code>
/// </summary>
public static class LedonTransactionServiceCollectionExtensions
{
    public static IServiceCollection AddLedonTransactions(
        this IServiceCollection services,
        Action<LedonTransactionOptions>? configure = null,
        Action<LedonSagaOptions>? configureSaga = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new LedonTransactionOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        // 选项同时走 IOptions：调用方的 Configure<T> / 配置文件都能覆盖默认值
        // （直接注册实例的话 IOptions 会另造一份，Configure 改了也不生效）
        services.AddOptions();
        services.Configure<LedonSagaOptions>(saga => configureSaga?.Invoke(saga));
        services.TryAddSingleton<LedonSagaOptions>(
            provider => provider.GetRequiredService<IOptions<LedonSagaOptions>>().Value);

        services.TryAddScoped<IUnitOfWork, EfUnitOfWork>();

        // 模块系统在场（IEventBus / 约定扫描注册事件处理器）——幂等
        services.AddLedon();

        // 候选 DbContext（自动发现）：一个作用域内解析第一个可用的。
        // 实现 ILedonDbContext 的优先——它是"我就是主业务上下文"的显式声明，
        // 比按类型名排序可靠（否则加一个 Alphabetically 更靠前的上下文就会悄悄换掉主上下文）
        var candidates = LedonAssemblies.Scan()
            .SelectMany(assembly => assembly.GetLoadableTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false }
                && typeof(DbContext).IsAssignableFrom(type))
            .OrderByDescending(type => typeof(ILedonDbContext).IsAssignableFrom(type))
            .ThenBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        UnitOfWorkRegistry.Set(candidates);

        // Saga 持久化：SagaDbContext 子类自动注册为基类型（EfSagaStore 依赖基类型解析）。
        // 没有子类时用进程内存储——语义与 Workflow 一致（无持久化则重启后实例丢失）
        var hasSagaStore = LedonAssemblies.Scan()
            .SelectMany(assembly => assembly.GetLoadableTypes())
            .Any(type => type is { IsClass: true, IsAbstract: false }
                && type != typeof(SagaDbContext)
                && typeof(SagaDbContext).IsAssignableFrom(type));

        if (hasSagaStore)
        {
            foreach (var assembly in LedonAssemblies.Scan())
            {
                foreach (var type in assembly.GetLoadableTypes()
                    .Where(type => type is { IsClass: true, IsAbstract: false }
                        && type != typeof(SagaDbContext)
                        && typeof(SagaDbContext).IsAssignableFrom(type)))
                {
                    services.TryAddScoped(typeof(SagaDbContext), provider => provider.GetRequiredService(type));
                }
            }

            services.TryAddScoped<ISagaStore, EfSagaStore>();
        }
        else
        {
            services.TryAddSingleton<ISagaStore, InMemorySagaStore>();
        }

        // Saga 工厂必须是 Scoped：它持有 ISagaStore（进而 DbContext）。
        // 注册成 Singleton 会让所有请求共享同一个 DbContext——线程安全问题 +
        // 跨请求状态泄漏（EF 的并发检测器会直接抛出来）
        services.TryAddScoped<ISagaFactory, SagaFactory>();

        // 崩溃/重启恢复：Running 且租约过期的实例由托管服务认领后继续推进
        services.AddHostedService<SagaRecoveryService>();

        return services;
    }

    /// <summary>
    /// 显式指定主 DbContext（多上下文场景；不指定则用第一个发现的）。
    /// </summary>
    public static IServiceCollection AddLedonTransactions<TContext>(
        this IServiceCollection services,
        Action<LedonTransactionOptions>? configure = null)
        where TContext : DbContext
    {
        services.AddLedonTransactions(configure);
        services.TryAddSingleton<Func<IServiceProvider, DbContext>>(
            provider => provider.GetRequiredService<TContext>());

        return services;
    }
}
