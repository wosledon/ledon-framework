using Asp.Versioning;
using FluentValidation;
using Ledon.Data;
using Ledon.Module;
using Ledon.Module.Abstractions;
using Ledon.Queues;
using Ledon.Scheduling;
using Ledon.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.ComponentModel.DataAnnotations;

namespace Ledon.Web.Tests.Fixtures;

// ---------- 功能包测试夹具 ----------

/// <summary>测试自有的运行时记录器（显式注册，验证队列任务在独立作用域内可解析服务）。</summary>
public sealed class Recorder
{
    private readonly List<string> _entries = [];

    public void Add(string entry)
    {
        lock (_entries)
            _entries.Add(entry);
    }

    public string[] Snapshot()
    {
        lock (_entries)
            return _entries.ToArray();
    }
}

/// <summary>缓存测试用的可序列化负载。</summary>
public class Payload
{
    public string Name { get; set; } = string.Empty;
}

/// <summary>健康检查夹具：被 AddLedonHealthChecks 自动发现。</summary>
public class SampleHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(HealthCheckResult.Healthy());
}

/// <summary>故障夹具：被自动发现后让聚合结果变 Unhealthy——用于验证存活/就绪分离。</summary>
public class BrokenHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(HealthCheckResult.Unhealthy("演示故障"));
}

// ---------- 端点（自动发现 + 自动映射） ----------

/// <summary>空模块：钩子全用默认实现，供只需一个模块在场的场景使用。</summary>
public class NoopModule : IModule;

/// <summary>抛异常的端点：验证全局异常处理转 ProblemDetails。</summary>
public class ThrowEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder builder) =>
        builder.MapGet("/throw", () =>
        {
            throw new InvalidOperationException("演示异常");
        });
}

public class HelloEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder builder)
        => builder.MapGet("/hello", () => "world");
}

// ---------- 控制器（ApplicationPart 自动发现） ----------

[ApiController]
[Route("api/ping")]
public class PingController : ControllerBase
{
    [HttpGet]
    public string Get() => "pong";
}

/// <summary>模型验证：空 Name 应得到 400 ValidationProblemDetails。</summary>
public class EchoRequest
{
    [Required]
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// FluentValidation 验证器（自动发现）：Name 不能为 forbidden——
/// DataAnnotations 覆盖不到的规则，验证 FV 经同一模型验证管线产出 400。
/// </summary>
public class EchoRequestValidator : AbstractValidator<EchoRequest>
{
    public EchoRequestValidator()
    {
        RuleFor(x => x.Name).NotEqual("forbidden");
    }
}

[ApiController]
[Route("api/echo")]
public class EchoController : ControllerBase
{
    [HttpPost]
    public EchoRequest Post(EchoRequest request) => request;
}

/// <summary>统一响应包装：启用后返回值包一层 {"code":0,"data":...}。</summary>
[ApiController]
[Route("api/values")]
public class ValuesController : ControllerBase
{
    [HttpGet]
    public object Get() => new { Value = 42 };
}

/// <summary>API 版本化：URL 段 api/v{version}/versioned。</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/versioned")]
public class VersionedController : ControllerBase
{
    [HttpGet]
    public string Get() => "v1";
}

// ---------- 定时任务夹具 ----------

/// <summary>每秒触发一次（6 段 cron：秒 分 时 日 月 周）。</summary>
public class EverySecondTask : IScheduledTask
{
    private readonly TaskCompletionSource _fired = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string CronExpression => "*/1 * * * * *";

    public Task Fired => _fired.Task;

    public Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        _fired.TrySetResult();
        return Task.CompletedTask;
    }
}

/// <summary>
/// 慢任务：首次执行阻塞在闸门上（时长 6s，跨过一个 4s 周期的触发点），
/// 用于验证默认丢弃与补跑（CatchUpMissedTriggers）两种 misfire 语义。
/// 后续执行立即完成并发出 <see cref="SecondRun"/> 信号。
/// </summary>
public class SlowTask : IScheduledTask
{
    private int _invocations;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _secondRun = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string CronExpression => "*/4 * * * * *";

    public int Invocations => Volatile.Read(ref _invocations);

    public Task Started => _started.Task;

    public Task SecondRun => _secondRun.Task;

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _invocations) == 1)
        {
            _started.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        else
        {
            _secondRun.TrySetResult();
        }
    }
}

/// <summary>测试用领导权锁：固定授予/拒绝（验证多实例门控逻辑，不依赖真实 Redis）。</summary>
public sealed class FakeSchedulerLock(bool granted) : ISchedulerLock
{
    public Task<bool> AcquireAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(granted);

    public Task<bool> RenewAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(granted);

    public Task ReleaseAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

// ---------- 发件箱（Outbox）夹具 ----------

/// <summary>测试用发件箱 DbContext（Sqlite 文件在测试中配置）。</summary>
public class TestOutboxDbContext(DbContextOptions<TestOutboxDbContext> options) : OutboxDbContext(options);

public record OrderPlaced(string OrderId) : IIntegrationEvent;

/// <summary>集成事件投递信号（测试串行执行，静态安全）。</summary>
public static class OutboxSignal
{
    public static bool Delivered { get; set; }
}

public class OrderPlacedHandler : IIntegrationEventHandler<OrderPlaced>
{
    public Task HandleAsync(OrderPlaced @event, CancellationToken cancellationToken = default)
    {
        OutboxSignal.Delivered = true;
        return Task.CompletedTask;
    }
}

/// <summary>测试用传输：记录投递的事件（验证 dispatcher 注册传输后走 broker 而非进程内 handler）。</summary>
public sealed class RecordingTransport : IIntegrationEventTransport
{
    private readonly List<IIntegrationEvent> _published = [];
    private readonly object _gate = new();

    public IReadOnlyList<IIntegrationEvent> Published
    {
        get { lock (_gate) return _published.ToArray(); }
    }

    public Task PublishAsync(IIntegrationEvent @event, CancellationToken cancellationToken = default)
    {
        lock (_gate)
            _published.Add(@event);

        return Task.CompletedTask;
    }
}

// ---------- 认证授权夹具 ----------

/// <summary>受保护端点：JWT 或 API Key 任一方式认证；permission 端点要求 secure.read 权限。</summary>
[ApiController]
[Route("api/secure")]
public class SecureController : ControllerBase
{
    [Authorize]
    [HttpGet("whoami")]
    public object WhoAmI() => new
    {
        User.Identity?.Name,
        Permissions = User.Claims.Where(claim => claim.Type == "permission").Select(claim => claim.Value),
    };

    [Authorize(Policy = "secure.read")]
    [HttpGet("permission")]
    public string Permission() => "has-permission";
}

[ApiController]
[Route("api/todos")]
public class TodosController(TodoDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<List<TodoItem>> GetAll(CancellationToken cancellationToken) =>
        await db.Todos.ToListAsync(cancellationToken);

    [HttpPost]
    public async Task<TodoItem> Create(TodoItem item, CancellationToken cancellationToken)
    {
        db.Todos.Add(item);
        await db.SaveChangesAsync(cancellationToken);
        return item;
    }
}

// ---------- 数据（ILedonDbContext 自动注册 + 前置自动建库） ----------

public class TodoDbContext : DbContext, ILedonDbContext
{
    public DbSet<TodoItem> Todos => Set<TodoItem>();

    protected override void OnConfiguring(DbContextOptionsBuilder options) =>
        options.UseSqlite($"Data Source={TodoDbPath.Value};Pooling=False");
}

public class TodoItem
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
}

/// <summary>数据库文件路径（测试夹具在启动前重置）。</summary>
public static class TodoDbPath
{
    public static string Value { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "ledon-webtest.db");
}

// ---------- 前置顺序验证：模块初始化时 schema 必须已就绪 ----------

public static class DbReady
{
    public static bool SchemaReady { get; set; }
}

/// <summary>
/// 在初始化阶段直接查库。若 DbInitializer（ILedonPreInitialization）没有先于模块执行，
/// 表不存在会抛 SQLite 异常 → 宿主启动失败 → 测试失败。
/// 显式注册的测试探针，不参与自动发现。
/// </summary>
[DisableAutoDiscovery]
public class DbConsumerModule : IModule
{
    public async Task OnInitializedAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        using var scope = context.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TodoDbContext>();
        _ = await db.Todos.CountAsync(cancellationToken);
        DbReady.SchemaReady = true;
    }
}
