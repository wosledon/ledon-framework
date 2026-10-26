namespace Ledon.Search.Sync;

/// <summary>内存同步队列：进程重启丢失；生产建议用发件箱/持久队列实现。</summary>
public sealed class InMemorySearchSyncQueue(TimeProvider timeProvider, SearchSyncOptions options) : ISearchSyncQueue
{
    private readonly object _gate = new();
    private readonly List<SearchSyncQueueItem> _items = [];
    private readonly HashSet<long> _leased = [];
    private long _nextId;

    public int PendingCount
    {
        get
        {
            lock (_gate)
                return _items.Count;
        }
    }

    public Task EnqueueAsync(SearchIndexChange change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentException.ThrowIfNullOrWhiteSpace(change.Index);
        ArgumentException.ThrowIfNullOrWhiteSpace(change.DocumentId);

        if (change.Kind == SearchChangeKind.Upsert && change.Document is null)
            throw new ArgumentException("Upsert 变更必须提供 Document", nameof(change));

        lock (_gate)
        {
            _items.Add(new SearchSyncQueueItem
            {
                Id = ++_nextId,
                Change = change,
                NextAttemptUtc = timeProvider.GetUtcNow(),
            });
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SearchSyncQueueItem>> DequeueAsync(
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();

        lock (_gate)
        {
            var batch = _items
                .Where(item => !_leased.Contains(item.Id) && item.NextAttemptUtc <= now)
                .OrderBy(item => item.NextAttemptUtc)
                .ThenBy(item => item.Id)
                .Take(Math.Max(1, maxCount))
                .ToArray();

            foreach (var item in batch)
                _leased.Add(item.Id);

            return Task.FromResult<IReadOnlyList<SearchSyncQueueItem>>(batch);
        }
    }

    public Task CompleteAsync(SearchSyncQueueItem item, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _leased.Remove(item.Id);
            _items.RemoveAll(candidate => candidate.Id == item.Id);
        }

        return Task.CompletedTask;
    }

    public Task FailAsync(SearchSyncQueueItem item, Exception error, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _leased.Remove(item.Id);
            item.Attempts++;
            item.LastError = error.Message;

            var delay = TimeSpan.FromTicks(
                Math.Min(
                    options.MaxRetryDelay.Ticks,
                    options.RetryDelay.Ticks * (long)Math.Pow(2, Math.Max(0, item.Attempts - 1))));

            item.NextAttemptUtc = timeProvider.GetUtcNow() + delay;
        }

        return Task.CompletedTask;
    }
}
