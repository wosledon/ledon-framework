# Ledon.Scheduling

Ledon Framework 的定时任务：cron 驱动、自动发现、每任务独立循环。

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

- 支持 5 段与 6 段（含秒）cron 表达式；时区可配（默认 UTC）；
- 每任务独立 DI 作用域与 span，单任务失败不影响其它任务与调度；
- cron 无效启动即失败，不拖到运行时静默不执行；
- 执行天然串行；耗时跨过触发点时默认丢弃，`o.CatchUpMissedTriggers = true` 可补跑。

完整文档：<https://github.com/wosledon/ledon-framework>
