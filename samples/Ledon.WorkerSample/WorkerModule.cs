using Ledon.Caching;
using Ledon.Module;
using Ledon.Module.Abstractions;
using Ledon.Queues;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ledon.WorkerSample;

/// <summary>应用自有模块（宿主程序集）：演示队列、缓存、数据在模块里的用法。</summary>
public class WorkerModule : IModule
{
    public async Task OnInitializedAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        context.Logger.LogInformation("WorkerModule 初始化（宿主程序集）");

        // 缓存：未命中回源，同 key 并发只回源一次
        var cache = context.Services.GetRequiredService<ICacheService>();
        var greeting = await cache.GetOrCreateAsync(
            "greeting",
            _ => Task.FromResult("来自缓存的问候"),
            cancellationToken: cancellationToken);
        context.Logger.LogInformation("缓存读取：{Greeting}", greeting);

        // 后台队列：任务在独立 DI 作用域中执行，失败不影响宿主
        var queue = context.Services.GetRequiredService<IBackgroundQueue>();
        await queue.EnqueueAsync(async (scope, token) =>
        {
            var logger = scope.GetRequiredService<ILogger<WorkerModule>>();
            var db = scope.GetRequiredService<WorkerDbContext>();

            db.Logs.Add(new WorkLog { Message = "后台任务写入" });
            await db.SaveChangesAsync(token);

            logger.LogInformation("后台任务完成：已写入工作日志（独立作用域）");
        }, cancellationToken);

        // 数据：在独立作用域里写一条日志（模块初始化阶段容器已构建，按作用域解析 Scoped 服务）
        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkerDbContext>();
        db.Logs.Add(new WorkLog { Message = "WorkerModule 初始化" });
        await db.SaveChangesAsync(cancellationToken);
    }
}
