using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Data;

/// <summary>
/// 把审计拦截器与连接弹性接入全部已注册的 DbContext（EF Core 的
/// <see cref="IDbContextOptionsConfiguration{TContext}"/> 机制）。
/// 用户自己 <c>AddDbContext&lt;T&gt;(o =&gt; o.UseXxx(...))</c> 配连接串时同样生效——
/// 这是拦截器不放在注册回调里而走该机制的原因（EF 不会自动发现 DI 里的拦截器）。
/// </summary>
internal sealed class LedonAuditOptionsConfiguration<TContext>(
    LedonDataOptions options,
    LedonAuditInterceptor interceptor,
    DomainEventsDispatcherInterceptor? domainEventsDispatcher = null) : IDbContextOptionsConfiguration<TContext>
    where TContext : DbContext
{
    public void Configure(IServiceProvider serviceProvider, DbContextOptionsBuilder optionsBuilder)
    {
        // 审计：时间戳/操作人自动填充 + 软删除转换
        optionsBuilder.AddInterceptors(interceptor);

        // 领域事件分发（SaveChanges 成功后经 IEventBus）
        if (options.DispatchDomainEvents && domainEventsDispatcher is not null)
            optionsBuilder.AddInterceptors(domainEventsDispatcher);

        // 连接弹性：瞬断重试（提供程序需带 EnableRetryOnFailure 扩展）
        if (options.EnableRetryOnFailure)
            optionsBuilder.EnableRetryOnFailure(options.RetryMaxAttempts);
    }
}