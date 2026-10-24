using Ledon.Module;
using Ledon.Module.Abstractions;
using Ledon.Queues;
using Ledon.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddLedon();          // 模块系统（零配置）
builder.Services.AddLedonQueues();    // 后台队列（按需）
builder.Services.AddLedonScheduling(); // 定时任务（按需）

using var host = builder.Build();
await host.StartAsync();              // 模块自动初始化
await host.WaitForShutdownAsync();    // 常驻直到 Ctrl+C

// ---------- 模块 ----------

public class WorkerModule : IModule
{
    public async Task OnInitializedAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        context.Logger.LogInformation("WorkerModule 初始化");

        // 后台任务：独立 DI 作用域中执行
        var queue = context.Services.GetRequiredService<IBackgroundQueue>();
        await queue.EnqueueAsync(async (scope, token) =>
        {
            var logger = scope.GetRequiredService<ILogger<WorkerModule>>();
            logger.LogInformation("后台任务执行中");
            await Task.CompletedTask;
        }, cancellationToken);
    }
}

// ---------- 定时任务 ----------

public class HeartbeatTask : IScheduledTask
{
    public string CronExpression => "*/1 * * * *";   // 每分钟

    public Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"heartbeat {DateTime.Now:HH:mm:ss}");
        return Task.CompletedTask;
    }
}
