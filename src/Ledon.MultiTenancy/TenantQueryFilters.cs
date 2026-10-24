using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace Ledon.MultiTenancy;

/// <summary>
/// 多租户实体标记：实现即表达"这张表按租户隔离"。
/// 与 <c>Ledon.Data.ISoftDelete</c> 同风格——标记接口声明意图，过滤由
/// <see cref="ModelBuilderExtensions.ApplyTenantFilters{TContext}"/> 显式启用。
/// </summary>
public interface IMultiTenant
{
    /// <summary>租户 ID。</summary>
    string TenantId { get; set; }
}

/// <summary>EF Core 模型构建扩展（多租户过滤）。</summary>
public static class ModelBuilderExtensions
{
    /// <summary>
    /// 为所有 <see cref="IMultiTenant"/> 实体应用租户查询过滤。在 <c>OnModelCreating</c> 里显式调用：
    /// <code>
    /// public class AppDbContext(DbContextOptions&lt;AppDbContext&gt; options) : DbContext(options)
    /// {
    ///     // 每请求/每作用域设置（Web 由解析中间件、后台由 SetAmbient 穿透后写入）
    ///     public string? CurrentTenantId { get; set; }
    ///
    ///     protected override void OnModelCreating(ModelBuilder modelBuilder) =&gt;
    ///         modelBuilder.ApplyTenantFilters(this, context =&gt; context.CurrentTenantId);
    /// }
    /// </code>
    /// 与多租户整体设计边界一致：过滤显式声明，不隐式生效（避免"查出来的行数悄悄变少"）。
    /// <para>
    /// 实现要点：过滤表达式引用**上下文实例成员**（EF 文档的查询过滤模式）——EF 每次查询
    /// 按当前上下文实例重新求值。不要传闭包捕获实例字段的 <c>Func</c>：EF 按上下文类型
    /// 缓存模型与编译后的查询计划，Func 的调用结果会被首个查询固化，之后一直读旧值。
    /// </para>
    /// <para>
    /// 无租户上下文时的语义由 <paramref name="filterWhenNoTenant"/> 决定：
    /// <list type="bullet">
    ///   <item><c>false</c>（默认）：不加过滤——宿主/后台穿透视角可见全部行（单租户应用不受影响）；</item>
    ///   <item><c>true</c>：过滤为空——未解析出租户的上下文看不到任何行（严格隔离）。</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="modelBuilder">模型构建器。</param>
    /// <param name="context">当前 DbContext 实例（传 <c>this</c>）。</param>
    /// <param name="tenantIdSelector">上下文中承载当前租户 ID 的成员（如 <c>ctx =&gt; ctx.CurrentTenantId</c>）。</param>
    /// <param name="filterWhenNoTenant">无租户时是否过滤为空，默认 false。</param>
    public static ModelBuilder ApplyTenantFilters<TContext>(
        this ModelBuilder modelBuilder,
        TContext context,
        Expression<Func<TContext, string?>> tenantIdSelector,
        bool filterWhenNoTenant = false)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenantIdSelector);

        if (tenantIdSelector.Body is not MemberExpression member)
            throw new ArgumentException(
                "tenantIdSelector 必须是上下文成员访问（如 ctx => ctx.CurrentTenantId），不能是方法调用或闭包字段——" +
                "EF 只对上下文成员做按查询重新求值。", nameof(tenantIdSelector));

        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
            .Where(type => typeof(IMultiTenant).IsAssignableFrom(type.ClrType)
                && !type.ClrType.IsAbstract))
        {
            var parameter = Expression.Parameter(entityType.ClrType, "entity");
            var entityTenant = Expression.Property(parameter, nameof(IMultiTenant.TenantId));

            // 当前租户：((TContext)context).成员——EF 每次查询按当前上下文实例重新求值
            var contextConstant = Expression.Constant(context, typeof(TContext));
            var currentTenant = Expression.Property(contextConstant, member.Member.Name);

            var equal = Expression.Equal(entityTenant, currentTenant);
            Expression body = equal;

            if (!filterWhenNoTenant)
            {
                // currentTenant == null || entity.TenantId == currentTenant
                var isNull = Expression.Equal(
                    currentTenant, Expression.Constant(null, typeof(string)));
                body = Expression.OrElse(isNull, equal);
            }

            modelBuilder.Entity(entityType.ClrType).HasQueryFilter(
                Expression.Lambda(body, parameter));
        }

        return modelBuilder;
    }
}
