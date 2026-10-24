using System.Reflection;
using Ledon.Module.Hosting;

namespace Ledon.Module;

/// <summary>
/// 供功能包（Ledon.Web / Ledon.Data 等）复用的程序集扫描 API：
/// 返回与模块系统一致的"业务程序集"集合（自动发现双通道 + 显式追加），
/// 避免每个功能包各写一套发现逻辑。
/// </summary>
public static class LedonAssemblies
{
    public static IReadOnlyList<Assembly> Scan(Action<LedonOptions>? configure = null)
    {
        var options = new LedonOptions();
        configure?.Invoke(options);
        return AssemblyScanner.ResolveAssemblies(options).Assemblies;
    }
}
