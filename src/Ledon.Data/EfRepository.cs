using System.Linq.Expressions;
using Ledon;
using Ledon.Module;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ledon.Data;

/// <summary>
/// 通用仓储：把"按 ID 取 / 按规约查 / 增删 / 保存"这些重复代码收干净。
/// 注册：<c>services.AddLedonRepositories()</c>（自动发现 DbContext 并为每个实体注册）。
/// 需要更复杂的查询时直接注入 DbContext 或自定义仓储——本类型不是强制层。
/// </summary>
public interface IRepository<T> where T : class
{
    /// <summary>当前 DbSet（需要进一步自组查询时用）。</summary>
    IQueryable<T> Query();

    /// <summary>
    /// 按主键取（**不跟踪**，尊重全局查询过滤器，如软删除/多租户）。
    /// 不存在或被过滤器排除返回 null。只读场景默认走这条路径。
    /// 复合主键传数组，顺序与主键定义一致。
    /// </summary>
    Task<T?> GetByIdAsync(object? key, CancellationToken cancellationToken = default);

    /// <summary>按主键数组取（复合主键；不跟踪，尊重全局查询过滤器）。</summary>
    Task<T?> GetByIdAsync(object?[] keyValues, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按主键取（**跟踪查询**，用于随后修改并保存）。尊重全局查询过滤器。
    /// </summary>
    Task<T?> GetByIdForUpdateAsync(object? key, CancellationToken cancellationToken = default);

    /// <summary>按主键数组取（复合主键；跟踪查询，用于修改）。</summary>
    Task<T?> GetByIdForUpdateAsync(object?[] keyValues, CancellationToken cancellationToken = default);

    /// <summary>按规约查列表（不跟踪）。</summary>
    Task<IReadOnlyList<T>> ListAsync(ISpecification<T> specification, CancellationToken cancellationToken = default);

    /// <summary>按规约取单条（不跟踪）；不存在返回 null。</summary>
    Task<T?> FirstOrDefaultAsync(ISpecification<T> specification, CancellationToken cancellationToken = default);

    /// <summary>按条件取单条（不跟踪）；不存在返回 null。</summary>
    Task<T?> FirstOrDefaultAsync(
        System.Linq.Expressions.Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default);

    /// <summary>按规约取单条（跟踪，用于修改）；不存在返回 null。</summary>
    Task<T?> FirstOrDefaultForUpdateAsync(ISpecification<T> specification, CancellationToken cancellationToken = default);

    /// <summary>按条件取单条（跟踪，用于修改）；不存在返回 null。</summary>
    Task<T?> FirstOrDefaultForUpdateAsync(
        System.Linq.Expressions.Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default);

    /// <summary>是否存在满足条件的记录。</summary>
    Task<bool> AnyAsync(
        System.Linq.Expressions.Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default);

    /// <summary>计数。</summary>
    Task<int> CountAsync(
        System.Linq.Expressions.Expression<Func<T, bool>>? predicate = null,
        CancellationToken cancellationToken = default);

    /// <summary>分页查询（不跟踪）。</summary>
    Task<PagedResult<T>> PagedAsync(
        int page,
        int pageSize,
        System.Linq.Expressions.Expression<Func<T, bool>>? predicate = null,
        CancellationToken cancellationToken = default);

    /// <summary>加入上下文（保存后落库）。</summary>
    Task AddAsync(T entity, CancellationToken cancellationToken = default);

    /// <summary>加入上下文（多个）。</summary>
    Task AddRangeAsync(IEnumerable<T> entities, CancellationToken cancellationToken = default);

    /// <summary>标记删除（软删除实体由拦截器转换为更新）。</summary>
    void Remove(T entity);

    /// <summary>标记删除（多个）。</summary>
    void RemoveRange(IEnumerable<T> entities);

    /// <summary>保存变更。</summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary><see cref="IRepository{T}"/> 的 EF 实现。</summary>
internal sealed class EfRepository<T>(DbContext context) : IRepository<T> where T : class
{
    private DbSet<T> Set => context.Set<T>();

    public IQueryable<T> Query() => Set;

    public Task<T?> GetByIdAsync(object? key, CancellationToken cancellationToken = default) =>
        GetByIdAsync([key], cancellationToken);

    public Task<T?> GetByIdAsync(object?[] keyValues, CancellationToken cancellationToken = default) =>
        QueryByKey(keyValues).AsNoTracking().FirstOrDefaultAsync(cancellationToken);

    public Task<T?> GetByIdForUpdateAsync(object? key, CancellationToken cancellationToken = default) =>
        GetByIdForUpdateAsync([key], cancellationToken);

    public Task<T?> GetByIdForUpdateAsync(object?[] keyValues, CancellationToken cancellationToken = default) =>
        QueryByKey(keyValues).FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<T>> ListAsync(
        ISpecification<T> specification,
        CancellationToken cancellationToken = default) =>
        await Set.ApplySpecification(specification)
            .AsNoTracking()
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<T?> FirstOrDefaultAsync(
        ISpecification<T> specification,
        CancellationToken cancellationToken = default) =>
        await Set.ApplySpecification(specification)
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    public Task<T?> FirstOrDefaultAsync(
        System.Linq.Expressions.Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default) =>
        Set.AsNoTracking().FirstOrDefaultAsync(predicate, cancellationToken);

    public async Task<T?> FirstOrDefaultForUpdateAsync(
        ISpecification<T> specification,
        CancellationToken cancellationToken = default) =>
        await Set.ApplySpecification(specification)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    public Task<T?> FirstOrDefaultForUpdateAsync(
        System.Linq.Expressions.Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default) =>
        Set.FirstOrDefaultAsync(predicate, cancellationToken);

    public Task<bool> AnyAsync(
        System.Linq.Expressions.Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default) =>
        Set.AnyAsync(predicate, cancellationToken);

    public Task<int> CountAsync(
        System.Linq.Expressions.Expression<Func<T, bool>>? predicate = null,
        CancellationToken cancellationToken = default) =>
        predicate is null ? Set.CountAsync(cancellationToken) : Set.CountAsync(predicate, cancellationToken);

    public async Task<PagedResult<T>> PagedAsync(
        int page,
        int pageSize,
        System.Linq.Expressions.Expression<Func<T, bool>>? predicate = null,
        CancellationToken cancellationToken = default)
    {
        var query = predicate is null ? Set.AsQueryable() : Set.Where(predicate);
        return await query.AsNoTracking().ToPagedResultAsync(page, pageSize, cancellationToken).ConfigureAwait(false);
    }

    public async Task AddAsync(T entity, CancellationToken cancellationToken = default) =>
        await Set.AddAsync(entity, cancellationToken).ConfigureAwait(false);

    public async Task AddRangeAsync(IEnumerable<T> entities, CancellationToken cancellationToken = default) =>
        await Set.AddRangeAsync(entities, cancellationToken).ConfigureAwait(false);

    public void Remove(T entity) => Set.Remove(entity);

    public void RemoveRange(IEnumerable<T> entities) => Set.RemoveRange(entities);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        context.SaveChangesAsync(cancellationToken);

    /// <summary>
    /// 按主键构建查询（而不是 <c>FindAsync</c>）：始终走数据库、尊重全局查询过滤器
    /// （软删除/多租户），也不会把取消令牌误当成主键值。
    /// </summary>
    private IQueryable<T> QueryByKey(object?[] keyValues)
    {
        ArgumentNullException.ThrowIfNull(keyValues);

        var entityType = context.Model.FindEntityType(typeof(T))
            ?? throw new InvalidOperationException($"实体 {typeof(T).Name} 未在当前 DbContext 中注册");

        var primaryKey = entityType.FindPrimaryKey()
            ?? throw new InvalidOperationException($"实体 {typeof(T).Name} 没有主键");

        if (primaryKey.Properties.Count != keyValues.Length)
        {
            throw new ArgumentException(
                $"主键数量不匹配：{typeof(T).Name} 需要 {primaryKey.Properties.Count} 个值，实际 {keyValues.Length} 个",
                nameof(keyValues));
        }

        var parameter = Expression.Parameter(typeof(T), "entity");
        Expression? predicate = null;

        for (var index = 0; index < keyValues.Length; index++)
        {
            var property = primaryKey.Properties[index];

            // 用 EF.Property 访问，兼容基类属性与影子主键
            var propertyAccess = Expression.Call(
                typeof(EF),
                nameof(EF.Property),
                [property.ClrType],
                parameter,
                Expression.Constant(property.Name));

            var value = Expression.Constant(keyValues[index], property.ClrType);
            var equals = Expression.Equal(propertyAccess, value);

            predicate = predicate is null ? equals : Expression.AndAlso(predicate, equals);
        }

        return Set.Where(Expression.Lambda<Func<T, bool>>(predicate!, parameter));
    }
}

/// <summary>仓储注册扩展。</summary>
public static class RepositoryServiceCollectionExtensions
{
    /// <summary>
    /// 为扫描到的每个实体类型注册 <see cref="IRepository{T}"/>（Scoped，随 DbContext 作用域）。
    /// 实体集合的判据：扫描到的 DbContext 上任意 <c>DbSet&lt;T&gt;</c> 属性的元素类型。
    /// 仓储通过该实体所属的 DbContext 解析（按 DbSet 归属匹配）。
    /// </summary>
    public static IServiceCollection AddLedonRepositories(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 实体类型 → 声明它的 DbContext 类型（一个实体只归属一个上下文）
        var entityOwners = LedonAssemblies.Scan()
            .SelectMany(assembly => assembly.GetLoadableTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false }
                && typeof(DbContext).IsAssignableFrom(type))
            .SelectMany(contextType => contextType.GetProperties()
                .Where(property => property.PropertyType.IsGenericType
                    && property.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
                .Select(property => (Entity: property.PropertyType.GetGenericArguments()[0], Owner: contextType)))
            .GroupBy(pair => pair.Entity)
            .ToDictionary(group => group.Key, group => group.First().Owner);

        foreach (var (entityType, ownerType) in entityOwners)
        {
            services.TryAddScoped(
                typeof(IRepository<>).MakeGenericType(entityType),
                provider =>
                {
                    var context = (DbContext)provider.GetRequiredService(ownerType);
                    return Activator.CreateInstance(
                        typeof(EfRepository<>).MakeGenericType(entityType), context)
                        ?? throw new InvalidOperationException($"无法创建 {entityType.Name} 的仓储");
                });
        }

        return services;
    }
}
