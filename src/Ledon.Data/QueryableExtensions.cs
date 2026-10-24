using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace Ledon.Data;

/// <summary>分页结果。</summary>
public sealed class PagedResult<T>
{
    public PagedResult(IReadOnlyList<T> items, int page, int pageSize, long totalCount)
    {
        Items = items;
        Page = page;
        PageSize = pageSize;
        TotalCount = totalCount;
    }

    /// <summary>当前页数据。</summary>
    public IReadOnlyList<T> Items { get; }

    /// <summary>页码（1 起）。</summary>
    public int Page { get; }

    /// <summary>每页条数。</summary>
    public int PageSize { get; }

    /// <summary>总条数（不过滤分页）。</summary>
    public long TotalCount { get; }

    /// <summary>总页数。</summary>
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling(TotalCount / (double)PageSize) : 0;

    public bool HasPrevious => Page > 1;

    public bool HasNext => Page < TotalPages;

    /// <summary>空结果（页数越界等）。</summary>
    public static PagedResult<T> Empty(int page, int pageSize) => new([], page, pageSize, 0);
}

/// <summary>IQueryable 的查询扩展：规约应用、分页、动态排序。</summary>
public static class QueryableExtensions
{
    /// <summary>应用规约（条件 → 包含 → 排序 → 分页 → tracking/过滤设置）。</summary>
    public static IQueryable<T> ApplySpecification<T>(this IQueryable<T> source, ISpecification<T> specification) where T : class
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(specification);

        var query = source;

        if (specification.IgnoreQueryFilters)
            query = query.IgnoreQueryFilters();

        if (specification.Criteria is not null)
            query = query.Where(specification.Criteria);

        query = specification.Includes.Aggregate(
            query, (current, include) => current.Include(include));

        query = specification.IncludeStrings.Aggregate(
            query, (current, include) => current.Include(include));

        if (specification.Orderings.Count > 0)
        {
            var first = specification.Orderings.First();
            var ordered = first.Descending
                ? query.OrderByDescending(first.KeySelector)
                : query.OrderBy(first.KeySelector);

            query = specification.Orderings.Skip(1).Aggregate(
                ordered,
                (current, ordering) => ordering.Descending
                    ? current.ThenByDescending(ordering.KeySelector)
                    : current.ThenBy(ordering.KeySelector));
        }

        if (specification.Skip is { } skip)
            query = query.Skip(skip);

        if (specification.Take is { } take)
            query = query.Take(take);

        return specification.AsNoTracking ? query.AsNoTracking() : query;
    }

    /// <summary>分页查询（自动算总数）。</summary>
    public static async Task<PagedResult<T>> ToPagedResultAsync<T>(
        this IQueryable<T> source,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        var totalCount = await source.LongCountAsync(cancellationToken).ConfigureAwait(false);
        if (totalCount == 0)
            return PagedResult<T>.Empty(page, pageSize);

        var items = await source
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PagedResult<T>(items, page, pageSize, totalCount);
    }

    /// <summary>
    /// 动态排序：把 <c>"Name"</c>/<c>"Name desc"</c> 这样的字符串排序转成 OrderBy 表达式
    /// （属性名编译期未知，如 API 的 <c>?sort=</c> 参数）。
    /// 无效字段名返回原查询（不抛——排序不该让列表接口 500）。
    /// </summary>
    /// <param name="source">查询源。</param>
    /// <param name="sortBy">字段名，可带 " desc"/" asc" 后缀；空 = 不排序。</param>
    /// <param name="defaultOrderBy">无效字段名时的默认排序（null = 不排序）。</param>
    public static IQueryable<T> ApplyOrdering<T>(
        this IQueryable<T> source,
        string? sortBy,
        Expression<Func<T, object?>>? defaultOrderBy = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        var (field, descending) = Parse(sortBy);

        if (field is null)
            return defaultOrderBy is null
                ? source
                : source.OrderBy(defaultOrderBy);

        var parameter = Expression.Parameter(typeof(T), "x");
        var member = typeof(T).GetProperty(field,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.IgnoreCase);

        if (member is null)
            return defaultOrderBy is null
                ? source
                : source.OrderBy(defaultOrderBy);

        var body = Expression.Convert(Expression.Property(parameter, member), typeof(object));
        var keySelector = Expression.Lambda<Func<T, object?>>(body, parameter);

        return descending ? source.OrderByDescending(keySelector) : source.OrderBy(keySelector);
    }

    /// <summary>解析 "Name desc" → ("Name", true)；空/空白 → (null, false)。</summary>
    internal static (string? Field, bool Descending) Parse(string? sortBy)
    {
        if (string.IsNullOrWhiteSpace(sortBy))
            return (null, false);

        var parts = sortBy.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var field = parts[0];
        var descending = parts.Length > 1
            && parts[1].StartsWith("desc", StringComparison.OrdinalIgnoreCase);

        return (field, descending);
    }
}
