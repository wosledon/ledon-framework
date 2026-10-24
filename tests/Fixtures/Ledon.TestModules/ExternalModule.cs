using Ledon.Module;
using Ledon.Module.Abstractions;
using Microsoft.Extensions.Logging;

namespace Ledon.TestModules;

/// <summary>
/// 回归夹具：测试项目引用本程序集，但测试代码中只按"名字"断言、从不使用其类型——
/// 精确复现真实应用"项目引用了模块程序集但代码没直接用"的场景
/// （编译器会删掉未被使用的程序集引用，运行库也不会加载它）。
/// </summary>
public class ExternalModule : IModule
{
    public Task OnInitializedAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        context.Logger.LogInformation("ExternalModule 初始化（来自被引用但代码未使用的程序集）");
        return Task.CompletedTask;
    }
}
