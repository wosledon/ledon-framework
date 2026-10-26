namespace Ledon.Search.Sync;

/// <summary>索引变更类型。</summary>
public enum SearchChangeKind
{
    Upsert,
    Delete,
}

/// <summary>搜索引擎同步选项。</summary>
public sealed class SearchSyncOptions
{
    /// <summary>是否随宿主启动后台同步器，默认 <c>true</c>（需已注册 ISearchService）。</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>每批处理数量，默认 32。</summary>
    public int BatchSize { get; set; } = 32;

    /// <summary>空闲轮询间隔，默认 1 秒。</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>最大重试次数，默认 5。</summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>首次重试延迟，默认 2 秒（指数退避）。</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>最大重试延迟，默认 1 分钟。</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>一条索引变更（可作为发件箱消息载荷）。</summary>
public sealed record SearchIndexChange(
    string Index,
    string DocumentId,
    SearchChangeKind Kind,
    object? Document = null,
    string? TenantId = null,
    DateTimeOffset? EnqueuedAtUtc = null);

/// <summary>队列中的变更（含重试状态）。</summary>
public sealed class SearchSyncQueueItem
{
    public required long Id { get; init; }

    public required SearchIndexChange Change { get; init; }

    public int Attempts { get; internal set; }

    public DateTimeOffset NextAttemptUtc { get; internal set; }

    public string? LastError { get; internal set; }
}

/// <summary>同步队列（默认内存；生产可替换为数据库发件箱/Redis）。</summary>
public interface ISearchSyncQueue
{
    Task EnqueueAsync(SearchIndexChange change, CancellationToken cancellationToken = default);

    /// <summary>取出到期变更（标记为处理中）。</summary>
    Task<IReadOnlyList<SearchSyncQueueItem>> DequeueAsync(
        int maxCount,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(SearchSyncQueueItem item, CancellationToken cancellationToken = default);

    Task FailAsync(SearchSyncQueueItem item, Exception error, CancellationToken cancellationToken = default);

    int PendingCount { get; }
}

/// <summary>单条同步错误。</summary>
public sealed record SearchSyncError(string Index, string DocumentId, int Attempts, string Error);

/// <summary>一批同步结果。</summary>
public sealed record SearchSyncBatchResult(
    int Succeeded,
    int Failed,
    int Dropped,
    IReadOnlyList<SearchSyncError> Errors)
{
    public int Processed => Succeeded + Failed + Dropped;
}
