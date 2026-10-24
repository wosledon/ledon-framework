using System.Reflection;
using Ledon.Module.Abstractions;
using Ledon.Module.Events;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Module.Hosting;

/// <summary>
/// <c>AddLedon</c> 的配置。默认全自动（发现 + 注入），下面所有方法都是可选的逃生门：
/// <code>
/// builder.Services.AddLedon();                                        // 零配置，日常用法
/// builder.Services.AddLedon(o => o.DisableAutoDiscovery()             // 关闭全局扫描
///     .AddModule&lt;UserModule&gt;());                                  // 只注册显式列出的
/// </code>
/// </summary>
public sealed class LedonOptions
{
    internal List<Assembly> Assemblies { get; } = [];

    internal List<Type> ExplicitModules { get; } = [];

    internal bool AutoDiscovery { get; private set; } = true;

    /// <summary>默认服务生命周期（约定注册未显式指定时使用），默认 <see cref="ServiceLifetime.Scoped"/>。</summary>
    public ServiceLifetime DefaultServiceLifetime { get; set; } = ServiceLifetime.Scoped;

    /// <summary>事件总线行为（处理器错误策略），默认失败即停。</summary>
    public EventOptions Events { get; } = new();

    /// <summary>
    /// 并行初始化（默认关）：开启后同一依赖层（互不依赖）的模块并发执行
    /// <c>OnInitializedAsync</c>，层与层之间仍保持拓扑序——模块多、初始化含 IO 等待时
    /// 启动时间明显缩短。注意：同层模块的初始化顺序不再确定（日志/诊断顺序会变），
    /// 模块间若有未声明的隐式依赖请用 <c>[DependsOn]</c> 显式声明。
    /// </summary>
    public bool ParallelInitialization { get; set; }

    /// <summary>追加一个要扫描的模块程序集（与自动发现叠加生效）。</summary>
    public LedonOptions AddAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        Assemblies.Add(assembly);
        return this;
    }

    /// <summary>追加 <typeparamref name="TModule"/> 所在的程序集到扫描列表。</summary>
    public LedonOptions AddAssembly<TModule>()
        where TModule : IModule => AddAssembly(typeof(TModule).Assembly);

    /// <summary>显式注册一个模块（与自动发现叠加；可注册带 <see cref="DisableAutoDiscoveryAttribute"/> 的类型）。</summary>
    public LedonOptions AddModule<TModule>()
        where TModule : IModule
    {
        ExplicitModules.Add(typeof(TModule));
        return this;
    }

    /// <summary>
    /// 关闭"入口程序集及其引用"的自动扫描。此后只处理 <see cref="AddAssembly"/> /
    /// <see cref="AddModule{TModule}"/> 显式指定的内容。
    /// </summary>
    public LedonOptions DisableAutoDiscovery()
    {
        AutoDiscovery = false;
        return this;
    }
}
