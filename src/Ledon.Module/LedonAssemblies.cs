using System.Reflection;
using Ledon.Module.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Module;

/// <summary>
/// 供功能包（Ledon.Web / Ledon.Data 等）复用的程序集扫描 API：
/// 返回与模块系统一致的"业务程序集"集合（自动发现双通道 + 显式追加），
/// 避免每个功能包各写一套发现逻辑。
/// <para>
/// **与模块系统共享同一份显式程序集清单**：<c>AddLedon(o =&gt; o.AddModule&lt;X&gt;())</c>
/// 登记的模块程序集会被这里看到。此前各功能包自己 <c>new LedonOptions()</c>，
/// 显式模式（<c>DisableAutoDiscovery</c>）下模块所在的程序集在它们的扫描里
/// 静默丢失——控制器/端点/DbContext/处理器一个都不注册，且没有任何告警。
/// </para>
/// </summary>
public static class LedonAssemblies
{
    /// <summary>
    /// <c>AddLedon</c> 登记的那一份选项。功能包在 <c>ConfigureServices</c> 阶段扫描，
    /// 那时还没有 <see cref="IServiceProvider"/> 可用，所以用进程级引用共享
    /// （一个进程只有一个模块系统；<c>AddLedon</c> 本身幂等）。
    /// </summary>
    private static LedonOptions? _current;

    /// <summary>由 <c>AddLedon</c> 调用：把最终选项共享给功能包的扫描。</summary>
    internal static void SetCurrent(LedonOptions options) => _current = options;

    public static IReadOnlyList<Assembly> Scan(Action<LedonOptions>? configure = null) =>
        AssemblyScanner.ResolveAssemblies(Prepare(configure)).Assemblies;

    /// <summary>从容器解析共享选项（运行期扫描用，拿得到更精确的那份）。</summary>
    public static IReadOnlyList<Assembly> Scan(IServiceProvider services, Action<LedonOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var registered = services.GetService<LedonOptions>();

        if (registered is not null)
            _current ??= registered;

        return Scan(configure);
    }

    /// <summary>以共享选项为基底复制一份，再套用调用方的追加（不污染共享实例）。</summary>
    private static LedonOptions Prepare(Action<LedonOptions>? configure)
    {
        var options = _current?.Clone() ?? new LedonOptions();
        configure?.Invoke(options);

        return options;
    }
}
