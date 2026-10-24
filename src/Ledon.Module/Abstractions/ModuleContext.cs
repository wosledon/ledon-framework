using System.Diagnostics;
using Ledon.Core.Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ledon.Module.Abstractions;

/// <summary>
/// 模块运行阶段（初始化/关闭）的执行上下文，始终持有已构建的服务容器。
/// </summary>
public sealed class ModuleContext
{
    private readonly IServiceProvider _services;
    private ILogger? _logger;

    public ModuleContext(ModuleDescriptor module, IServiceProvider services)
    {
        Module = module ?? throw new ArgumentNullException(nameof(module));
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>当前正在执行钩子的模块描述。</summary>
    public ModuleDescriptor Module { get; }

    /// <summary>已构建的服务容器，可直接解析服务。</summary>
    public IServiceProvider Services => _services;

    /// <summary>以模块名命名的 <see cref="ILogger{TCategoryName}"/>；未配置日志工厂时退化为 <c>NullLogger</c>。</summary>
    public ILogger Logger
    {
        get
        {
            _logger ??= _services.GetService<ILoggerFactory>()?.CreateLogger(Module.Name)
                ?? NullLogger.Instance;
            return _logger;
        }
    }

    /// <summary>宿主配置（若已注册 <c>IConfiguration</c>），否则为 null。</summary>
    public IConfiguration? Configuration => _services.GetService<IConfiguration>();

    /// <summary>事件总线：发布事件即被（已自动接线的）处理器消费，发布方无需知道谁在处理。</summary>
    public IEventBus Events => _services.GetRequiredService<IEventBus>();

    /// <summary>
    /// 以当前模块名开始一个自定义 span（挂在框架链路源的模块源下），
    /// 用于把模块内的关键工作纳入同一棵链路树。未接入 OpenTelemetry 时返回 null（无开销）。
    /// </summary>
    public Activity? StartActivity(string name) =>
        Diagnostics.LedonActivity.Modules.StartActivity($"{Module.Name}.{name}");
}
