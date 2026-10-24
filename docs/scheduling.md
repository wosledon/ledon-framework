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

## 已知边界

任务循环**不防重入**：上一次执行未结束时到点仍会并发再跑一次。执行时间必须显著小于触发间隔，或任务内部自行加分布式锁/数据库行锁。需要框架级防重入可关注后续版本。

## 长周期语义

到点判断基于 `Task.Delay` 到下一个发生时刻，宿主机休眠/挂起错过的时间点**不会补偿触发**（无 misfire 回补）。对"绝不能错过"的周期语义，用外部调度器（系统 cron / K8s CronJob）触发 HTTP 端点更稳妥。
