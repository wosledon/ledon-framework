using Ledon.Data;
using Ledon.Module.Abstractions;
using Ledon.Workflow;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>
/// 工作流引擎测试：happy path、步骤失败重试、Saga 补偿、EF 持久化与重启恢复。
/// 夹具模拟 protoc/用户代码形状（步骤 + 定义实现类，自动发现）。
/// </summary>
public class WorkflowTests : IDisposable
{
    private WebApplication? _app;

    private async Task<WebApplication> StartAsync(string dbPath)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonWorkflow();
        builder.Services.AddLedonData();   // 前置自动建库（工作流表随 WorkflowDbContext 建立）
        builder.Services.AddDbContext<WorkflowTestDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));

        var app = builder.Build();
        _app = app;
        await app.StartAsync();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowTestDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        return app;
    }

    [Fact]
    public async Task Happy_path_runs_all_steps_to_completion()
    {
        var app = await StartAsync(NewDb("happy"));

        var engine = app.Services.CreateScope().ServiceProvider.GetRequiredService<WorkflowEngine>();
        var instance = await engine.CreateAsync("happy-flow");
        var result = await engine.RunAsync(instance.Id);

        Assert.Equal(WorkflowStatus.Completed, result.Status);
        Assert.Equal(new[] { "step-a", "step-b" }, result.CompletedSteps);
        Assert.Equal("a→b", result.Data["trace"]);
    }

    [Fact]
    public async Task Failure_retries_then_compensates_executed_steps()
    {
        var app = await StartAsync(NewDb("saga"));

        var engine = app.Services.CreateScope().ServiceProvider.GetRequiredService<WorkflowEngine>();
        var instance = await engine.CreateAsync("saga-flow");

        var ex = await Assert.ThrowsAsync<WorkflowExecutionException>(
            () => engine.RunAsync(instance.Id));

        Assert.Equal("boom", ex.Instance.LastError);
        Assert.Equal("boom", ex.Step);

        // 重试耗尽后逆序补偿：step-a 被补偿，实例标记 Compensated
        var stored = await app.Services.CreateScope().ServiceProvider
            .GetRequiredService<IWorkflowStore>().GetAsync(instance.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkflowStatus.Compensated, stored.Status);
        Assert.Contains("compensate-a", StubWorkflowState.Compensated);
    }

    [Fact]
    public async Task Retry_succeeds_when_step_recovers()
    {
        StubWorkflowState.Attempts = 0;
        var app = await StartAsync(NewDb("retry"));

        var engine = app.Services.CreateScope().ServiceProvider.GetRequiredService<WorkflowEngine>();
        var instance = await engine.CreateAsync("flaky-flow");
        var result = await engine.RunAsync(instance.Id);

        Assert.Equal(WorkflowStatus.Completed, result.Status);
        Assert.Equal(2, StubWorkflowState.Attempts);   // 第一次失败，第二次成功
    }

    [Fact]
    public async Task Instance_persists_across_engine_instances()
    {
        var dbPath = NewDb("persist");
        var app = await StartAsync(dbPath);

        Guid instanceId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var engine = scope.ServiceProvider.GetRequiredService<WorkflowEngine>();
            var instance = await engine.CreateAsync("happy-flow", new Dictionary<string, object?> { ["k"] = "v" });
            instanceId = instance.Id;
            await engine.RunAsync(instanceId);
        }

        // 用新的作用域（模拟重启后的新引擎实例）从存储读回
        await using var scope2 = app.Services.CreateAsyncScope();
        var store = scope2.ServiceProvider.GetRequiredService<IWorkflowStore>();
        var reloaded = await store.GetAsync(instanceId);

        Assert.NotNull(reloaded);
        Assert.Equal(WorkflowStatus.Completed, reloaded.Status);
        Assert.Equal("v", reloaded.Data["k"]?.ToString());
        Assert.Equal(new[] { "step-a", "step-b" }, reloaded.CompletedSteps);
    }

    [Fact]
    public async Task Claim_is_exclusive_and_expires()
    {
        var app = await StartAsync(NewDb("claim"));

        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowStore>();

        var engine = scope.ServiceProvider.GetRequiredService<WorkflowEngine>();
        var instance = await engine.CreateAsync("happy-flow");

        // 第一个认领者拿到，第二个拿不到（多节点部署时只有一个节点会恢复同一个实例）
        Assert.True(await store.ClaimAsync(instance.Id, "node-a", TimeSpan.FromMinutes(1)));
        Assert.False(await store.ClaimAsync(instance.Id, "node-b", TimeSpan.FromMinutes(1)));

        // 同一持有者可以续约
        Assert.True(await store.ClaimAsync(instance.Id, "node-a", TimeSpan.FromMinutes(1)));

        // 租约极短：过期后别人就能接手（节点崩溃后的接管路径）
        Assert.True(await store.ClaimAsync(instance.Id, "node-a", TimeSpan.FromMilliseconds(1)));
        await Task.Delay(30);
        Assert.True(await store.ClaimAsync(instance.Id, "node-b", TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task Concurrent_saves_are_rejected_instead_of_lost()
    {
        var app = await StartAsync(NewDb("concurrency"));

        // 两个作用域 = 两个 DbContext = 两个节点各自读一份再改
        WorkflowInstance first;
        WorkflowInstance second;

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var engine = scope.ServiceProvider.GetRequiredService<WorkflowEngine>();
            var instance = await engine.CreateAsync("happy-flow");
            first = (await scope.ServiceProvider.GetRequiredService<IWorkflowStore>().GetAsync(instance.Id))!;
        }

        await using (var scope = app.Services.CreateAsyncScope())
        {
            second = (await scope.ServiceProvider.GetRequiredService<IWorkflowStore>().GetAsync(first.Id))!;
        }

        // 第一个节点先提交
        first.CompletedSteps.Add("step-a");
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWorkflowStore>().SaveAsync(first);
        }

        // 第二个节点后提交：必须失败，而不是静默覆盖前一个刚写入的 CompletedSteps
        second.CompletedSteps.Add("step-b");

        await using var failing = app.Services.CreateAsyncScope();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            failing.ServiceProvider.GetRequiredService<IWorkflowStore>().SaveAsync(second));

        // 存储里保留的是第一个节点写入的内容
        await using var verify = app.Services.CreateAsyncScope();
        var stored = await verify.ServiceProvider.GetRequiredService<IWorkflowStore>().GetAsync(first.Id);

        Assert.Equal(["step-a"], stored!.CompletedSteps);
    }

    [Fact]
    public async Task Resumable_excludes_instances_claimed_by_another_node()
    {
        var app = await StartAsync(NewDb("claim-filter"));

        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowStore>();
        var engine = scope.ServiceProvider.GetRequiredService<WorkflowEngine>();

        var claimed = await engine.CreateAsync("happy-flow");
        var free = await engine.CreateAsync("happy-flow");

        // GetResumableAsync 只看 Running：把两条都置成运行中
        foreach (var instance in new[] { claimed, free })
        {
            instance.Status = WorkflowStatus.Running;
            await store.SaveAsync(instance);
        }

        // 只有 node-a 认领过的那条不出现在可恢复列表里
        Assert.True(await store.ClaimAsync(claimed.Id, "node-a", TimeSpan.FromMinutes(5)));

        var resumable = await store.GetResumableAsync(10);

        Assert.DoesNotContain(resumable, instance => instance.Id == claimed.Id);
        Assert.Contains(resumable, instance => instance.Id == free.Id);
    }

    [Fact]
    public async Task Resuming_a_partially_done_instance_compensates_earlier_steps_too()
    {
        var app = await StartAsync(NewDb("recover-compensate"));

        Guid instanceId;

        // 模拟"上一轮 step-a 成功、进程在 boom 之前崩了"：实例停在 Running，
        // CompletedSteps 里有 step-a，CurrentStep 指向 boom
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var engine = scope.ServiceProvider.GetRequiredService<WorkflowEngine>();
            var instance = await engine.CreateAsync("saga-flow");
            instanceId = instance.Id;

            instance.Status = WorkflowStatus.Running;
            instance.CurrentStep = "boom";
            instance.CompletedSteps.Add("step-a");

            await scope.ServiceProvider.GetRequiredService<IWorkflowStore>().SaveAsync(instance);
        }

        StubWorkflowState.Compensated.Clear();

        // 恢复：这一轮只执行 boom（失败），但补偿必须覆盖**上一轮**完成的 step-a
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var engine = scope.ServiceProvider.GetRequiredService<WorkflowEngine>();

            await Assert.ThrowsAsync<WorkflowExecutionException>(() => engine.RunAsync(instanceId));
        }

        // 关键断言：跨调用补偿把上一轮的步骤也回滚了（修复前只补偿本次执行的步骤）
        Assert.Contains("compensate-a", StubWorkflowState.Compensated);
    }

    private static string NewDb(string name) =>
        Path.Combine(AppContext.BaseDirectory, $"workflow-{name}-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        if (_app is not null)
        {
            _app.StopAsync().GetAwaiter().GetResult();
            _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>测试夹具的共享状态（顺序执行，静态安全）。</summary>
public static class StubWorkflowState
{
    public static int Attempts { get; set; }

    public static List<string> Compensated { get; } = [];
}

// ---------- 夹具：步骤与定义 ----------

public class StepA : IWorkflowStep, ICompensableStep
{
    public string Name => "step-a";

    public Task<StepOutcome> ExecuteAsync(StepContext context, CancellationToken cancellationToken = default)
    {
        context.Data["trace"] = $"{(context.Data.TryGetValue("trace", out var t) ? t : null)}a→";
        return Task.FromResult(StepOutcome.Success);
    }

    public Task CompensateAsync(StepContext context, CancellationToken cancellationToken = default)
    {
        StubWorkflowState.Compensated.Add("compensate-a");
        return Task.CompletedTask;
    }
}

public class StepB : IWorkflowStep
{
    public string Name => "step-b";

    public Task<StepOutcome> ExecuteAsync(StepContext context, CancellationToken cancellationToken = default)
    {
        context.Data["trace"] = $"{(context.Data.TryGetValue("trace", out var t) ? t : null)}b";
        return Task.FromResult(StepOutcome.Success);
    }
}

/// <summary>总是失败的步骤（用于重试 + 补偿断言）。</summary>
public class BoomStep : IWorkflowStep
{
    public string Name => "boom";

    public Task<StepOutcome> ExecuteAsync(StepContext context, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("boom");
}

/// <summary>前两次失败、第三次成功的步骤（验证重试恢复）。</summary>
public class FlakyStep : IWorkflowStep
{
    public string Name => "flaky";

    public Task<StepOutcome> ExecuteAsync(StepContext context, CancellationToken cancellationToken = default)
    {
        StubWorkflowState.Attempts++;
        return Task.FromResult(StubWorkflowState.Attempts >= 2 ? StepOutcome.Success : StepOutcome.Failure);
    }
}

public class HappyFlow : IWorkflowDefinition
{
    public string Name => "happy-flow";
    public string InitialStep => "step-a";
    public IReadOnlyDictionary<string, string[]> Transitions => new Dictionary<string, string[]>
    {
        ["step-a"] = ["step-b"],
    };
}

public class SagaFlow : IWorkflowDefinition
{
    public string Name => "saga-flow";
    public string InitialStep => "step-a";
    public IReadOnlyDictionary<string, string[]> Transitions => new Dictionary<string, string[]>
    {
        ["step-a"] = ["boom"],
    };
}

public class FlakyFlow : IWorkflowDefinition
{
    public string Name => "flaky-flow";
    public string InitialStep => "step-a";
    public IReadOnlyDictionary<string, string[]> Transitions => new Dictionary<string, string[]>
    {
        ["step-a"] = ["flaky"],
    };
}

/// <summary>测试夹具上下文：标记禁用自动发现，避免污染其它用例的 AddLedonData 建库前置任务。</summary>
[DisableAutoDiscovery]
public class WorkflowTestDbContext(DbContextOptions<WorkflowTestDbContext> options) : WorkflowDbContext(options), ILedonDbContext;
