# 定时任务

```csharp
builder.Services.AddLedonScheduling();

public class CleanupTask : IScheduledTask
{
    public string CronExpression => "0 3 * * *";   // 每天 3 点（UTC）

    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        await db.CleanupAsync(ct);
    }
}
```

实现 `IScheduledTask` 即被自动发现，宿主启动后按各自 cron 驱动执行。

## 规则

- **cron 表达式**：支持 5 段（标准）与 6 段（含秒，如 `*/1 * * * * *`）——按字段数自动判别；
- **时区**：默认 UTC，`o.TimeZone` 可改（cron 按此时区解释，夏令自动处理）；
- **独立执行**：每个任务独立循环 + 独立 DI 作用域 + 独立 span，单任务失败只记日志，不影响其它任务与调度；
- **启动即失败**：cron 写错（解析不过）在启动时抛异常——不拖到运行时静默不执行。

## 执行语义

同一任务的执行**天然串行**——循环等待上一次执行结束后才走向下一个触发点，不会并发重跑。

执行耗时跨过后续触发点时的行为（misfire 策略）由 `CatchUpMissedTriggers` 控制：

| 设置                | 行为                                                                 |
| ------------------- | -------------------------------------------------------------------- |
| `false`（默认）     | 执行期间错过的触发点被丢弃；下一次执行为"执行完毕后"的下一个未来触发点 |
| `true`              | 执行完毕后立即补跑错过的触发（逐个、仍串行），追平后恢复正常节奏       |

```csharp
builder.Services.AddLedonScheduling(o => o.CatchUpMissedTriggers = true);
```

执行时间短于触发间隔时两种设置无差异。补跑适合"绝不能错过"的周期语义，但注意补跑期间新触发会继续积压、执行时间叠加——任务长期超时本身就是需要告警的问题。

## 多实例部署：领导权选举

默认每个实例独立执行全部定时任务（单实例语义）。多实例部署下避免重复执行，开启领导权选举：

```csharp
builder.Services.AddLedonScheduling(o =>
{
    o.LeaderElection = SchedulerLeaderElection.Redis;   // 仅领导实例执行
    o.RedisConnectionString = "localhost:6379";
});
```

- 基于 Redis 锁（SET NX PX）：抢到锁的实例成为领导，TTL 默认 30s、每 1/3 周期续约；
- **掉锁自动让出**：续约失败（网络分区/Redis 故障）时本实例停止任务循环，回到竞选；
- 领导权转移期间触发点不补跑（与默认 misfire 策略一致）；
- 自建锁（etcd、数据库、K8s Lease 等）可实现 `ISchedulerLock` 注册替换；
- 未配置时零开销（不注册锁即单实例语义）。

## 长周期语义

到点判断基于 `Task.Delay` 到下一个发生时刻，宿主机休眠/挂起错过的时间点**不会补偿触发**（无 misfire 回补）。对"绝不能错过"的周期语义，用外部调度器（系统 cron / K8s CronJob）触发 HTTP 端点更稳妥。

## 执行历史与手动触发

```csharp
// 历史：进程内有界环形缓冲（默认 100 条），运维排障/重放确认用
var history = provider.GetRequiredService<IScheduledTaskExecutionHistory>();
var last = history.Latest(nameof(ReconcileTask));   // TaskName/StartedAt/Duration/Success/Error/Manual

// 手动触发：等不到下一个 cron 触发点时立即执行一次
var invoker = provider.GetRequiredService<ISchedulerInvoker>();
await invoker.InvokeAsync<ReconcileTask>(ct);        // 或按名称（大小写不敏感）
await invoker.InvokeAsync("ReconcileTask", ct);      // 未注册的名称返回 false
```

- 手动触发与 cron 触发走同一套执行、历史与指标（`Manual = true` 标记）；
- 多实例部署时各实例只记自己的执行——全局视图接可观测性后端的 span/指标；
- 典型用法：把触发暴露成受权限保护的运维端点（"立刻重跑失败的对账任务"）。

## 指标

`Ledon.Scheduling` Meter（`AddLedonObservability()` 自动收集）：

| 指标                                        | 含义                                   |
| ------------------------------------------- | -------------------------------------- |
| `ledon.scheduler.task.executions`           | 执行次数（tag：task、status）          |
| `ledon.scheduler.task.missed_triggers`      | 执行耗时跨过触发点、被丢弃的触发数     |
| `ledon.scheduler.task.manual_invocations`   | 经 `ISchedulerInvoker` 的手动触发次数  |
| `ledon.scheduler.leadership.lost`           | 多实例部署下领导权让出次数             |

默认（丢弃）模式下，执行耗时跨过下一个触发点会记一次 `missed_triggers` 并打 Warning——这是任务长期超时的告警信号。
