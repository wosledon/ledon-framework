using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ledon.Search.Sync;

/// <summary>索引同步处理器：取队列、写搜索、失败退避重试。</summary>
public sealed class SearchSyncProcessor(
    ISearchService search,
    ISearchSyncQueue queue,
    SearchSyncOptions options)
{
    public async Task<SearchSyncBatchResult> ProcessBatchAsync(
        int? maxCount = null,
        CancellationToken cancellationToken = default)
    {
        var items = await queue.DequeueAsync(maxCount ?? options.BatchSize, cancellationToken).ConfigureAwait(false);

        if (items.Count == 0)
            return new SearchSyncBatchResult(0, 0, 0, []);

        var succeeded = 0;
        var failed = 0;
        var dropped = 0;
        var errors = new List<SearchSyncError>();

        foreach (var item in items)
        {
            try
            {
                await ApplyAsync(item.Change, cancellationToken).ConfigureAwait(false);
                await queue.CompleteAsync(item, cancellationToken).ConfigureAwait(false);
                succeeded++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await queue.FailAsync(item, ex, cancellationToken).ConfigureAwait(false);

                errors.Add(new SearchSyncError(
                    item.Change.Index,
                    item.Change.DocumentId,
                    item.Attempts,
                    ex.Message));

                if (item.Attempts >= options.MaxRetries)
                {
                    await queue.CompleteAsync(item, cancellationToken).ConfigureAwait(false);
                    dropped++;
                }
                else
                {
                    failed++;
                }
            }
        }

        return new SearchSyncBatchResult(succeeded, failed, dropped, errors);
    }

    private Task ApplyAsync(SearchIndexChange change, CancellationToken cancellationToken) => change.Kind switch
    {
        SearchChangeKind.Delete => search.DeleteAsync(change.Index, change.DocumentId, cancellationToken),
        _ => search.IndexAsync(
            change.Index,
            change.DocumentId,
            change.Document ?? throw new InvalidOperationException("Upsert 变更缺少 Document"),
            change.TenantId,
            cancellationToken),
    };
}

/// <summary>索引同步服务（可在后台 Worker 之外手动触发）。</summary>
public sealed class SearchSyncService(
    ISearchSyncQueue queue,
    SearchSyncProcessor processor,
    SearchSyncOptions options)
{
    public int PendingCount => queue.PendingCount;

    public Task EnqueueUpsertAsync<T>(
        string index,
        string documentId,
        T document,
        string? tenantId = null,
        CancellationToken cancellationToken = default) =>
        queue.EnqueueAsync(
            new SearchIndexChange(index, documentId, SearchChangeKind.Upsert, document, tenantId, DateTimeOffset.UtcNow),
            cancellationToken);

    public Task EnqueueDeleteAsync(
        string index,
        string documentId,
        string? tenantId = null,
        CancellationToken cancellationToken = default) =>
        queue.EnqueueAsync(
            new SearchIndexChange(index, documentId, SearchChangeKind.Delete, null, tenantId, DateTimeOffset.UtcNow),
            cancellationToken);

    public Task EnqueueAsync(SearchIndexChange change, CancellationToken cancellationToken = default) =>
        queue.EnqueueAsync(change, cancellationToken);

    public Task<SearchSyncBatchResult> ProcessOnceAsync(
        int? maxCount = null,
        CancellationToken cancellationToken = default) =>
        processor.ProcessBatchAsync(maxCount ?? options.BatchSize, cancellationToken);
}

/// <summary>后台索引同步 Worker（AutoStart 时注册）。</summary>
public sealed class SearchSyncWorker(
    SearchSyncProcessor processor,
    SearchSyncOptions options,
    ILogger<SearchSyncWorker>? logger = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await processor.ProcessBatchAsync(options.BatchSize, stoppingToken).ConfigureAwait(false);

                if (result.Processed == 0)
                    await Task.Delay(options.PollInterval, stoppingToken).ConfigureAwait(false);

                if (result.Dropped > 0)
                    logger?.LogWarning("搜索同步丢弃 {Count} 条超过重试上限的变更", result.Dropped);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "搜索同步批次失败");
                await Task.Delay(options.PollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>搜索同步注册。</summary>
public static class LedonSearchSyncServiceCollectionExtensions
{
    public static Microsoft.Extensions.DependencyInjection.IServiceCollection AddLedonSearchSync(
        this Microsoft.Extensions.DependencyInjection.IServiceCollection services,
        Action<SearchSyncOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new SearchSyncOptions();
        configure?.Invoke(options);

        if (options.BatchSize < 1)
            throw new InvalidOperationException("SearchSyncOptions.BatchSize 至少为 1");

        if (options.MaxRetries < 0)
            throw new InvalidOperationException("SearchSyncOptions.MaxRetries 不能为负数");

        if (options.PollInterval <= TimeSpan.Zero)
            throw new InvalidOperationException("SearchSyncOptions.PollInterval 必须大于 0");

        if (options.RetryDelay < TimeSpan.Zero || options.MaxRetryDelay < options.RetryDelay)
            throw new InvalidOperationException("SearchSyncOptions 的重试延迟配置无效");

        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, options);
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<
            TimeProvider>(services, TimeProvider.System);
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<
            ISearchSyncQueue,
            InMemorySearchSyncQueue>(services);
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<
            SearchSyncProcessor>(services);
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<
            SearchSyncService>(services);

        if (options.AutoStart)
            Microsoft.Extensions.DependencyInjection.ServiceCollectionHostedServiceExtensions.AddHostedService<
                SearchSyncWorker>(services);

        return services;
    }
}
