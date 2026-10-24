using Ledon.Module;
using Ledon.Module.Abstractions;
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
