# 工作流引擎

多步骤业务流程的顺序编排：步骤实现类自动发现、推进即持久化、失败重试 + Saga 补偿、重启恢复。

适用场景：订单履约（下单→扣款→发货→通知）、对账流程、KYC 审核、数据迁移管线——
任何"必须按顺序执行多步、中间失败要把已做的步骤撤掉"的业务。

## 接入

```csharp
builder.Services.AddLedonWorkflow();   // 步骤/定义自动发现 + 恢复服务
```

DbContext 继承 `WorkflowDbContext` 即获得实例表（随 `AddLedonData()` 前置建库）。

## 定义工作流

```csharp
public class OrderWorkflow : IWorkflowDefinition
{
    public string Name => "order";
    public string InitialStep => "reserve";
    public IReadOnlyDictionary<string, string[]> Transitions => new()
    {
        ["reserve"] = ["charge"],
        ["charge"] = ["ship", "cancel"],   // 多出口：由步骤写 Data["next"] 选择
    };
}
```

## 实现步骤

```csharp
public class ChargeStep(IpaymentGateway pay) : IWorkflowStep, ICompensableStep
{
    public string Name => "charge";

    public async Task<StepOutcome> ExecuteAsync(StepContext ctx, CancellationToken ct = default)
    {
        var orderId = (int)ctx.Data["orderId"]!;
        await pay.ChargeAsync(orderId, ct);
        return StepOutcome.Success;
    }

    // 失败回滚时被调用（应对重复调用幂等）
    public async Task CompensateAsync(StepContext ctx, CancellationToken ct = default) =>
        await pay.RefundAsync((int)ctx.Data["orderId"]!, ct);
}
```

- 实现 `IWorkflowStep` 即被自动发现（引用框架 = 选择加入）；
- 跨步骤数据放 `ctx.Data`（随实例持久化）；
- 动态出口：`ctx.Data["next"] = "cancel"` 覆盖转移表默认出口。

## 运行

```csharp
var engine = provider.GetRequiredService<WorkflowEngine>();
var instance = await engine.CreateAsync("order", new Dictionary<string, object?> { ["orderId"] = 42 });
try
{
    var done = await engine.RunAsync(instance.Id, ct);   // 推进到终止
    // done.Status == Completed
}
catch (WorkflowExecutionException ex)
{
    // 重试耗尽（默认 3 次/步）并补偿后抛出；ex.Instance 带完整现场
    // ex.Instance.Status == Compensated（有可补偿步骤）或 Failed
}
```

## 语义与边界

| 主题       | 行为                                                                 |
| ---------- | -------------------------------------------------------------------- |
| 持久化     | 每次推进后保存实例（含数据与已完成步骤）——进程崩溃后不丢进度          |
| 重启恢复   | 托管服务启动即把 `Running` 实例继续推进，之后每 30s 轮询              |
| 重试       | 单步骤失败重试至 `WorkflowOptions.MaxRetriesPerStep`（默认 3）        |
| 补偿       | 重试耗尽后**逆序**补偿已执行步骤（Saga）；补偿失败只记录不阻断其余    |
| 并发       | 引擎是 Scoped：同一实例不要并发 `RunAsync`（持久化非行级锁保护）      |
| 长时任务   | 步骤内长时间等待建议改为"写状态 → 外部事件驱动再推进"，别占着执行线程 |

## 与其它能力的关系

- **发件箱**：步骤里产生的跨服务事件走 `AddLedonOutbox()`（与业务数据同事务）；
- **定时任务**：周期性地推进/补偿批量实例用 `AddLedonScheduling()`；
- **后台队列**：把 `RunAsync` 丢进队列做异步执行（`AddLedonQueues()`）。

## 相关主题

- [后台队列与发件箱](~/docs/queues.md)——可靠投递
- [定时任务](~/docs/scheduling.md)——cron 驱动的周期语义
