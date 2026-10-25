using Ledon.AuditLog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 把审计拦截器接入全部已注册的 DbContext（EF 的 <see cref="IDbContextOptionsConfiguration{TContext}"/> 机制，
/// 用户自己 <c>AddDbContext</c> 配连接串也生效——EF 不会自动发现 DI 里的拦截器）。
/// </summary>
internal sealed class AuditOptionsConfiguration<TContext>(
    AuditSaveChangesInterceptor interceptor) : IDbContextOptionsConfiguration<TContext>
    where TContext : DbContext
{
    public void Configure(IServiceProvider serviceProvider, DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.AddInterceptors(interceptor);
}
