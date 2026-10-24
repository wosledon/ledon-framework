using System.Linq.Expressions;

namespace Ledon.Data;

/// <summary>
/// 查询规约：把"查什么"（条件、包含、排序、分页）封装成可复用、可组合的对象。
/// 仓储与 <see cref="QueryableExtensions.ApplySpecification"/> 都消费它——
/// 业务代码不必在每个方法里重复拼 IQueryable。
/// </summary>
public interface ISpecification<T> where T : class
{
    /// <summary>过滤条件（null = 不过滤）。</summary>
    Expression<Func<T, bool>>? Criteria { get; }

    /// <summary>需要 Include 的导航属性（避免 N+1 与懒加载陷阱）。</summary>
    IReadOnlyCollection<Expression<Func<T, object?>>> Includes { get; }

    /// <summary>ThenInclude 式的字符串包含（多级导航，如 "Orders.Items"）。</summary>
    IReadOnlyCollection<string> IncludeStrings { get; }

    /// <summary>排序（可多个，按添加顺序）。</summary>
    IReadOnlyCollection<Ordering<T>> Orderings { get; }

    /// <summary>分页：跳过条数（null = 不分页）。</summary>
    int? Skip { get; }

    /// <summary>分页：取条数（null = 不分页）。</summary>
    int? Take { get; }

    /// <summary>是否 no-tracking（默认 true：查询场景不该被变更追踪拖慢）。</summary>
    bool AsNoTracking { get; }

    /// <summary>是否在分页时忽略 QueryFilter（软删除/租户过滤）——默认 false。</summary>
    bool IgnoreQueryFilters { get; }
}

/// <summary>排序描述。</summary>
public sealed record Ordering<T>(Expression<Func<T, object?>> KeySelector, bool Descending);

/// <summary>规约基类：用受保护方法累积条件/包含/排序/分页。</summary>
public abstract class Specification<T> : ISpecification<T> where T : class
{
    private readonly List<Expression<Func<T, object?>>> _includes = [];
    private readonly List<string> _includeStrings = [];
    private readonly List<Ordering<T>> _orderings = [];

    public Expression<Func<T, bool>>? Criteria { get; private set; }

    public IReadOnlyCollection<Expression<Func<T, object?>>> Includes => _includes;

    public IReadOnlyCollection<string> IncludeStrings => _includeStrings;

    public IReadOnlyCollection<Ordering<T>> Orderings => _orderings;

    public int? Skip { get; private set; }

    public int? Take { get; private set; }

    public bool AsNoTracking { get; private set; } = true;

    public bool IgnoreQueryFilters { get; private set; }

    protected void Where(Expression<Func<T, bool>> criteria) => Criteria = criteria;

    protected void Include(Expression<Func<T, object?>> include) => _includes.Add(include);

    protected void Include(string includeString) => _includeStrings.Add(includeString);

    protected void OrderBy(Expression<Func<T, object?>> keySelector) =>
        _orderings.Add(new Ordering<T>(keySelector, Descending: false));

    protected void OrderByDescending(Expression<Func<T, object?>> keySelector) =>
        _orderings.Add(new Ordering<T>(keySelector, Descending: true));

    protected void ApplyPaging(int page, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        Skip = (page - 1) * pageSize;
        Take = pageSize;
    }

    protected void AsNoTrackingQuery() => AsNoTracking = true;

    protected void AsTracking() => AsNoTracking = false;

    protected void WithIgnoreQueryFilters() => IgnoreQueryFilters = true;
}
