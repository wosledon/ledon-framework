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

    /// <summary>按 ID 取；不存在返回 null。</summary>
    Task<T?> GetByIdAsync(params object?[] keyValues);

    /// <summary>按 ID 取（带取消令牌）。</summary>
    Task<T?> GetByIdAsync(object?[] keyValues, CancellationToken cancellationToken = default);

    /// <summary>按规约查列表。</summary>
    Task<IReadOnlyList<T>> ListAsync(ISpecification<T> specification, CancellationToken cancellationToken = default);

    /// <summary>按规约取单条；不存在返回 null。</summary>
    Task<T?> FirstOrDefaultAsync(ISpecification<T> specification, CancellationToken cancellationToken = default);

    /// <summary>按条件取单条；不存在返回 null。</summary>
    Task<T?> FirstOrDefaultAsync(
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

    /// <summary>分页查询。</summary>
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

    public Task<T?> GetByIdAsync(params object?[] keyValues) =>
        Set.FindAsync(keyValues).AsTask();

    public Task<T?> GetByIdAsync(object?[] keyValues, CancellationToken cancellationToken = default) =>
        Set.FindAsync(keyValues, cancellationToken).AsTask();

    public async Task<IReadOnlyList<T>> ListAsync(
        ISpecification<T> specification,
        CancellationToken cancellationToken = default) =>
        await Set.ApplySpecification(specification).ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<T?> FirstOrDefaultAsync(
        ISpecification<T> specification,
        CancellationToken cancellationToken = default) =>
        await Set.ApplySpecification(specification).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    public Task<T?> FirstOrDefaultAsync(
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
        return await query.ToPagedResultAsync(page, pageSize, cancellationToken).ConfigureAwait(false);
    }

    public async Task AddAsync(T entity, CancellationToken cancellationToken = default) =>
        await Set.AddAsync(entity, cancellationToken).ConfigureAwait(false);

    public async Task AddRangeAsync(IEnumerable<T> entities, CancellationToken cancellationToken = default) =>
        await Set.AddRangeAsync(entities, cancellationToken).ConfigureAwait(false);

    public void Remove(T entity) => Set.Remove(entity);

    public void RemoveRange(IEnumerable<T> entities) => Set.RemoveRange(entities);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        context.SaveChangesAsync(cancellationToken);
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
