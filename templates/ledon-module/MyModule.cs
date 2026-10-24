using Ledon.Module;
using Ledon.Module.Abstractions;
using Ledon.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ledon.MyModule;

/// <summary>
/// 业务模块：实现 IModule 即被宿主自动发现（宿主引用本程序集即可，零注册）。
/// 依赖用 [DependsOn] 声明；跨模块通信用事件（发布方不需要知道谁在处理）。
/// </summary>
public class MyModule : IModule
{
    public void ConfigureServices(IServiceCollection services)
    {
        // 约定注册：IMyService ↔ MyService 会被自动扫描；此处放显式注册
    }

    public Task OnInitializedAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        context.Logger.LogInformation("MyModule 初始化");
        return Task.CompletedTask;
    }
}

/// <summary>模块对外服务（同名接口约定会被自动注册）。</summary>
public interface IMyService;

public class MyService : IMyService;

/// <summary>定时任务：实现 IScheduledTask 即按 cron 自动执行（宿主需 AddLedonScheduling）。</summary>
public class MyCleanupTask : IScheduledTask
{
    // 5 段（分时日月周）或 6 段（含秒）；每天 03:00
    public string CronExpression => "0 3 * * *";

    public Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        // 执行历史自动记录（IScheduledTaskExecutionHistory），也可经 ISchedulerInvoker 手动触发
        return Task.CompletedTask;
    }
}
