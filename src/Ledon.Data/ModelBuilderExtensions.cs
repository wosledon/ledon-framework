using Microsoft.EntityFrameworkCore;

namespace Ledon.Data;

/// <summary>DbContext 模型构建扩展。</summary>
public static class ModelBuilderExtensions
{
    /// <summary>
    /// 为所有 <see cref="ISoftDelete"/> 实体应用软删除查询过滤
    /// （<c>!IsDeleted</c>）——在 <c>OnModelCreating</c> 里显式调用：
    /// <code>
    /// protected override void OnModelCreating(ModelBuilder modelBuilder)
    /// {
    ///     modelBuilder.ApplySoftDeleteFilters();
    /// }
    /// </code>
    /// 与多租户一致的设计边界：过滤显式声明，不隐式生效（避免"查出来的行数悄悄变少"）。
    /// </summary>
    public static ModelBuilder ApplySoftDeleteFilters(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
            .Where(type => type.ClrType.IsAssignableTo(typeof(ISoftDelete))
                && !type.ClrType.IsAbstract))
        {
            var parameter = System.Linq.Expressions.Expression.Parameter(entityType.ClrType, "entity");
            var body = System.Linq.Expressions.Expression.Not(
                System.Linq.Expressions.Expression.Property(parameter, nameof(ISoftDelete.IsDeleted)));

            modelBuilder.Entity(entityType.ClrType).HasQueryFilter(
                System.Linq.Expressions.Expression.Lambda(body, parameter));
        }

        return modelBuilder;
    }
}
