using Ledon.Core.Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ledon.Module.Abstractions;

/// <summary>
/// 模块钩子的执行上下文。注册阶段（ConfigureServices）由无服务容器的构造创建，
/// 此阶段访问 <see cref="Services"/> / <see cref="Events"/> 会抛出
/// <see cref="LedonModuleException"/>；初始化与关闭阶段持有已构建的服务容器。
/// </summary>
public sealed class ModuleContext
{
    private readonly IServiceProvider? _services;
    private ILogger? _logger;

    /// <summary>注册阶段（服务容器尚未构建）的上下文。</summary>
    public ModuleContext(ModuleDescriptor module)
    {
        Module = module ?? throw new ArgumentNullException(nameof(module));
    }

    /// <summary>运行阶段（服务容器已构建）的上下文。</summary>
    public ModuleContext(ModuleDescriptor module, IServiceProvider services)
    {
        Module = module ?? throw new ArgumentNullException(nameof(module));
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>当前正在执行钩子的模块描述。</summary>
    public ModuleDescriptor Module { get; }

    /// <summary>
    /// 已构建的服务容器。仅在初始化/关闭阶段可用；注册阶段访问会抛
    /// <see cref="LedonModuleException"/>。
    /// </summary>
    public IServiceProvider Services => _services ?? throw new LedonModuleException(
        $"模块 {Module.Name} 处于注册阶段（ConfigureServices），服务容器尚未构建；请在 OnInitializedAsync / OnShutdownAsync 中解析服务。");

    /// <summary>以模块名命名的 <see cref="ILogger{TCategoryName}"/>；未配置日志工厂时退化为 <c>NullLogger</c>。</summary>
    public ILogger Logger
    {
        get
        {
            if (_services is null)
                return NullLogger.Instance;

            return _logger ??= _services.GetService<ILoggerFactory>()?.CreateLogger(Module.Name)
                ?? NullLogger.Instance;
        }
    }

    /// <summary>宿主配置（若已注册 <c>IConfiguration</c>），否则为 null。</summary>
    public IConfiguration? Configuration => _services?.GetService<IConfiguration>();

    /// <summary>事件总线，用于模块间发布/订阅。运行阶段可用。</summary>
    public IEventBus Events => Services.GetRequiredService<IEventBus>();
}
