using Ledon.Module.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ledon.Transactions;

/// <summary><see cref="ISagaTransaction"/> 的默认实现。</summary>
internal sealed class SagaTransaction : ISagaTransaction
{
    private readonly IServiceProvider _services;
    private readonly ILogger<SagaTransaction> _logger;
    private readonly List<SagaStep> _steps = [];

    public SagaTransaction(IServiceProvider services, string name, ILogger<SagaTransaction> logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _services = services;
        Name = name;
        _logger = logger;
    }

    public string Name { get; }

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
        var completed = new List<(SagaStep Step, SagaStepContext Context)>();

        foreach (var step in _steps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var activity = LedonActivity.Modules.StartActivity($"Saga.{Name}.{step.Name}");
            activity?.SetTag("saga.name", Name);
            activity?.SetTag("saga.step", step.Name);

            await using var scope = _services.CreateAsyncScope();

            try
            {
                var context = CreateContext(scope.ServiceProvider, step.Name);
                await step.Execute(context, cancellationToken).ConfigureAwait(false);
                completed.Add((step, context));
            }
            catch (Exception ex)
            {
                activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
                _logger.LogWarning(ex, "Saga {Saga} 步骤 {Step} 失败，开始逆序补偿", Name, step.Name);

                await CompensateAsync(completed, cancellationToken).ConfigureAwait(false);

                throw new SagaExecutionException(Name, step.Name, ex);
            }
        }

        _logger.LogInformation("Saga {Saga} 完成（{Count} 步）", Name, completed.Count);
    }

    private SagaStepContext CreateContext(IServiceProvider services, string stepName) =>
        new(Name, stepName, services, Data, services.GetRequiredService<IUnitOfWork>());

    /// <summary>逆序补偿已完成步骤；补偿失败只记录不阻断其余补偿。</summary>
    private async Task CompensateAsync(
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
                _logger.LogInformation("Saga {Saga} 步骤 {Step} 已补偿", Name, step.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saga {Saga} 步骤 {Step} 补偿失败（需人工介入）", Name, step.Name);
            }
        }
    }

    private sealed record SagaStep(
        string Name,
        Func<SagaStepContext, CancellationToken, Task> Execute,
        Func<SagaStepContext, CancellationToken, Task>? Compensate);
}

/// <summary>Saga 工厂（入口：<c>saga.For("name")</c>）。</summary>
public interface ISagaFactory
{
    /// <summary>创建一个具名 Saga。</summary>
    ISagaTransaction For(string name);
}

internal sealed class SagaFactory(IServiceProvider services) : ISagaFactory
{
    public ISagaTransaction For(string name) =>
        new SagaTransaction(services, name, services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SagaTransaction>>());
}
