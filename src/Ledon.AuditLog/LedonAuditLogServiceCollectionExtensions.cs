using Ledon.AuditLog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 操作审计入口（谁在何时改了什么，属性级 before/after）：
/// <code>
/// builder.Services.AddLedonAudit();                    // 内存存储（开发/单实例）
/// builder.Services.AddLedonAudit&lt;AppDbContext&gt;();      // 持久化到该上下文的 AuditEntries
/// app.UseLedonAuditContext();                          // 自动填充操作人/IP
/// </code>
/// </summary>
public static class LedonAuditLogServiceCollectionExtensions
{
    /// <summary>注册审计服务（内存存储）。</summary>
    public static IServiceCollection AddLedonAudit(this IServiceCollection services)
        => services.AddLedonAudit(_ => { });

    /// <summary>注册审计服务并配置。</summary>
    public static IServiceCollection AddLedonAudit(
        this IServiceCollection services,
        Action<LedonAuditOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new LedonAuditOptions();
        configure(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<IAuditStore, InMemoryAuditStore>();
        services.TryAddSingleton<AuditSaveChangesInterceptor>();

        // 经 IDbContextOptionsConfiguration 接入全部已注册的 DbContext
        services.TryAdd(ServiceDescriptor.Singleton(
            typeof(IDbContextOptionsConfiguration<>),
            typeof(AuditOptionsConfiguration<>)));

        return services;
    }

    /// <summary>持久化到指定上下文的 <c>AuditEntries</c> 集合（该上下文需继承 <see cref="AuditLogDbContext"/> 或含该 DbSet）。</summary>
    public static IServiceCollection AddLedonAudit<TContext>(
        this IServiceCollection services,
        Action<LedonAuditOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddLedonAudit(configure ?? (_ => { }));

        // 具体上下文的存储（覆盖内存实现）
        services.RemoveAll<IAuditStore>();
        services.TryAddSingleton<IAuditStore, EfAuditStore<TContext>>();

        return services;
    }
}

