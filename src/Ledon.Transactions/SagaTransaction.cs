using System.Collections.Concurrent;
using Ledon.Module.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ledon.Transactions;

/// <summary>Saga 选项。</summary>
public sealed class LedonSagaOptions
{
    /// <summary>
    /// 单步骤执行超时，默认 5 分钟。步骤不观察取消令牌也不会把 Saga 永久卡住
    /// （超时后该步骤按失败处理，进入补偿）。
    /// </summary>
    public TimeSpan StepTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>认领租约时长：节点崩溃后最多这么久就有别的节点接手。</summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>恢复轮询间隔，默认 30 秒。</summary>
    public TimeSpan RecoveryPollingInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>单轮恢复的最大实例数，默认 20。</summary>
    public int RecoveryBatchSize { get; set; } = 20;
}

/// <summary><see cref="ISagaTransaction"/> 的默认实现（可持久化 + 可恢复）。</summary>
internal sealed class SagaTransaction : ISagaTransaction
{
    private readonly IServiceProvider _services;
    private readonly ISagaStore _store;
    private readonly LedonSagaOptions _options;
    private readonly ILogger<SagaTransaction> _logger;
    private readonly List<SagaStep> _steps = [];

    public SagaTransaction(
        IServiceProvider services,
        ISagaStore store,
        LedonSagaOptions options,
        string name,
        string idempotencyKey,
        ILogger<SagaTransaction> logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(idempotencyKey);

        _services = services;
        _store = store;
        _options = options;
        Name = name;
        IdempotencyKey = idempotencyKey;
        _logger = logger;
    }

    public string Name { get; }

    /// <summary>幂等键：同一个键只会有一个 Saga 实例（重复提交直接复用）。</summary>
    public string IdempotencyKey { get; }

    public IDictionary<string, object?> Data { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    public ISagaTransaction Step(
        string name,
        Func<SagaStepContext, CancellationToken, Task> execute,
        Func<SagaStepContext, CancellationToken, Task>? compensate = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(execute);

        _steps.Add(new SagaStep(name, execute, compensate));
        return this;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var instance = await ResolveInstanceAsync(cancellationToken).ConfigureAwait(false);

        // 已经结束过的实例不重复执行（幂等）
        if (instance.Status is SagaStatus.Completed or SagaStatus.Compensated or SagaStatus.Failed)
        {
            _logger.LogInformation("Saga {Saga}（{Key}）已是 {Status}，跳过重复执行",
                Name, IdempotencyKey, instance.Status);

            return;
        }

        instance.Status = SagaStatus.Running;

        // 认领续约：每次推进都把租约往后推（写 ??= 会让第一次之后再也不续约）
        instance.ClaimedBy ??= Owner;
        instance.ClaimedUntilUtc = DateTimeOffset.UtcNow.Add(_options.Lease);

        await _store.SaveAsync(instance, cancellationToken).ConfigureAwait(false);

        var completed = new List<(SagaStep Step, SagaStepContext Context)>();
        var plan = StepsInOrder(instance);

        foreach (var step in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var activity = LedonActivity.Modules.StartActivity($"Saga.{Name}.{step.Name}");
            activity?.SetTag("saga.name", Name);
            activity?.SetTag("saga.step", step.Name);
            activity?.SetTag("saga.instance", instance.Id.ToString());

            instance.CurrentStep = step.Name;

            // 已完成过的步骤（上次运行做的）不重复执行——这是崩溃恢复的关键
            if (instance.CompletedSteps.Contains(step.Name))
            {
                _logger.LogDebug("Saga {Saga} 步骤 {Step} 已完成过，跳过", Name, step.Name);
                continue;
            }

            await using var scope = _services.CreateAsyncScope();

            try
            {
                var context = CreateContext(scope.ServiceProvider, instance, step.Name);
                await ExecuteWithTimeoutAsync(step, context, cancellationToken).ConfigureAwait(false);

                instance.CompletedSteps.Add(step.Name);
                await _store.SaveAsync(instance, cancellationToken).ConfigureAwait(false);

                completed.Add((step, context));
            }
            catch (Exception ex)
            {
                activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
                instance.LastError = ex.Message;
                await _store.SaveAsync(instance, cancellationToken).ConfigureAwait(false);

                _logger.LogWarning(ex, "Saga {Saga} 步骤 {Step} 失败，开始逆序补偿", Name, step.Name);

                await CompensateAsync(instance, completed, cancellationToken).ConfigureAwait(false);

                instance.Status = SagaStatus.Compensated;
                await _store.SaveAsync(instance, cancellationToken).ConfigureAwait(false);

                throw new SagaExecutionException(Name, step.Name, ex);
            }
        }

        instance.Status = SagaStatus.Completed;
        instance.CurrentStep = null;
        await _store.SaveAsync(instance, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Saga {Saga} 完成（{Count} 步）", Name, instance.CompletedSteps.Count);
    }

    /// <summary>步骤执行 + 超时看门狗（步骤不观察取消令牌也不会永久卡住）。</summary>
    private async Task ExecuteWithTimeoutAsync(
        SagaStep step,
        SagaStepContext context,
        CancellationToken cancellationToken)
    {
        if (_options.StepTimeout <= TimeSpan.Zero)
        {
            await step.Execute(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.StepTimeout);

        try
        {
            await step.Execute(context, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Saga {Name} 步骤 {step.Name} 执行超过 {_options.StepTimeout.TotalSeconds} 秒");
        }
    }

    /// <summary>
    /// 按顺序取步骤：优先用持久化实例里的步骤清单（恢复路径），
    /// 否则用本次构建器注册的顺序（首次执行）。
    /// </summary>
    private List<SagaStep> StepsInOrder(SagaInstance instance)
    {
        if (_steps.Count > 0)
            return _steps;

        var byName = _steps.ToDictionary(step => step.Name, StringComparer.Ordinal);

        return instance.Steps
            .Where(name => byName.ContainsKey(name))
            .Select(name => byName[name])
            .ToList();
    }

    private async Task<SagaInstance> ResolveInstanceAsync(CancellationToken cancellationToken)
    {
        var existing = await _store.GetByIdempotencyKeyAsync(IdempotencyKey, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            SyncData(existing);
            return existing;
        }

        var instance = new SagaInstance
        {
            IdempotencyKey = IdempotencyKey,
            SagaName = Name,
        };

        instance.Steps.AddRange(_steps.Select(step => step.Name));

        SyncData(instance);

        try
        {
            await _store.SaveAsync(instance, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // 唯一索引冲突：并发提交用同一个幂等键抢先建了实例，改用它
            var raced = await _store.GetByIdempotencyKeyAsync(IdempotencyKey, cancellationToken)
                .ConfigureAwait(false);

            if (raced is null)
                throw;

            SyncData(raced);

            return raced;
        }

        return instance;
    }

    /// <summary>把构建器上的共享数据同步进实例（实例侧只存字符串）。</summary>
    private void SyncData(SagaInstance instance)
    {
        foreach (var (key, value) in Data)
            instance.Data[key] = value?.ToString() ?? "";
    }

    private SagaStepContext CreateContext(IServiceProvider services, SagaInstance instance, string stepName) =>
        new(Name, stepName, services, instance.Data, services.GetRequiredService<IUnitOfWork>());

    /// <summary>逆序补偿已完成步骤；补偿失败只记录不阻断其余补偿。</summary>
    private async Task CompensateAsync(
        SagaInstance instance,
        List<(SagaStep Step, SagaStepContext Context)> completed,
        CancellationToken cancellationToken)
    {
        for (var i = completed.Count - 1; i >= 0; i--)
        {
            var (step, context) = completed[i];

            if (step.Compensate is null)
            {
                _logger.LogWarning("Saga {Saga} 步骤 {Step} 未提供补偿操作（跳过，需人工介入）", Name, step.Name);
                continue;
            }

            try
            {
                await step.Compensate(context, cancellationToken).ConfigureAwait(false);
                instance.CompensatedSteps.Add(step.Name);
                await _store.SaveAsync(instance, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("Saga {Saga} 步骤 {Step} 已补偿", Name, step.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saga {Saga} 步骤 {Step} 补偿失败（需人工介入）", Name, step.Name);
            }
        }
    }

    /// <summary>本节点标识（同机多进程可区分）。</summary>
    private static string Owner => $"{Environment.MachineName}:{Environment.ProcessId}";

    private sealed record SagaStep(
        string Name,
        Func<SagaStepContext, CancellationToken, Task> Execute,
        Func<SagaStepContext, CancellationToken, Task>? Compensate);
}

/// <summary>Saga 工厂（入口：<c>saga.For("name")</c>）。</summary>
public interface ISagaFactory
{
    /// <summary>
    /// 创建一个具名 Saga。<paramref name="idempotencyKey"/> 相同则复用同一实例
    /// （重复提交不会把业务跑两遍）。
    /// </summary>
    ISagaTransaction For(string name, string? idempotencyKey = null);
}

internal sealed class SagaFactory(
    IServiceProvider services,
    ISagaStore store,
    LedonSagaOptions options) : ISagaFactory
{
    public ISagaTransaction For(string name, string? idempotencyKey = null) =>
        new SagaTransaction(
            services,
            store,
            options,
            name,
            idempotencyKey ?? Guid.NewGuid().ToString("N"),
            services.GetRequiredService<ILogger<SagaTransaction>>());
}

/// <summary>
/// Saga 恢复服务：进程重启/节点崩溃后，把存储里 Running 且租约过期的实例继续推进。
/// 每个实例先被**原子认领**——多节点部署时只有一个节点会恢复它。
/// </summary>
internal sealed class SagaRecoveryService(
    IServiceProvider services,
    LedonSagaOptions options,
    ILogger<SagaRecoveryService> logger) : BackgroundService
{
    private static readonly string Owner = $"{Environment.MachineName}:{Environment.ProcessId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(options.RecoveryPollingInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
                await RecoverAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Saga 恢复失败（下轮重试）");
            }
        }
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        // 每轮一个作用域：ISagaStore 是 Scoped（含 DbContext），
        // 不能在 BackgroundService（单例）里直接注入
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISagaStore>();

        IReadOnlyList<SagaInstance> resumable;

        try
        {
            resumable = await store.GetResumableAsync(options.RecoveryBatchSize, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Saga 恢复查询失败（schema 未就绪或存储不可用，下轮重试）");
            return;
        }

        if (resumable.Count == 0)
            return;

        var claimed = 0;

        foreach (var instance in resumable)
        {
            if (!await store.ClaimAsync(instance.Id, Owner, options.Lease, cancellationToken)
                    .ConfigureAwait(false))
                continue;

            claimed++;

            try
            {
                // 恢复路径上没有构建器：实例里持久化的步骤清单 + 幂等键就够了
                var saga = scope.ServiceProvider.GetRequiredService<ISagaFactory>()
                    .For(instance.SagaName, instance.IdempotencyKey);

                await saga.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Saga 实例 {Instance} 恢复失败（稍后重试）", instance.Id);
            }
        }

        if (claimed > 0)
            logger.LogInformation("Saga 恢复：认领并继续推进 {Claimed}/{Total} 个运行中实例", claimed, resumable.Count);
    }
}
