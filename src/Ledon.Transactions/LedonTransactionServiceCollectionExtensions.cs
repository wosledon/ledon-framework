using Ledon.Module;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        Action<LedonTransactionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new LedonTransactionOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        services.TryAddScoped<IUnitOfWork, EfUnitOfWork>();
        services.TryAddSingleton<ISagaFactory, SagaFactory>();

        // 模块系统在场（IEventBus / 约定扫描注册事件处理器）——幂等
        services.AddLedon();

        // 候选 DbContext（自动发现）：一个作用域内解析第一个可用的
        var candidates = LedonAssemblies.Scan()
            .SelectMany(assembly => assembly.GetLoadableTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false }
                && typeof(DbContext).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        UnitOfWorkRegistry.Set(candidates);

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
