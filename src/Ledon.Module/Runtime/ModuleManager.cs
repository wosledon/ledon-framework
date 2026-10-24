using System.Runtime.ExceptionServices;
using Ledon.Module.Abstractions;

namespace Ledon.Module.Runtime;

/// <summary>
/// <see cref="IModuleManager"/> 的默认实现：驱动初始化（拓扑序）与关闭（逆序）两个运行阶段，
/// 初始化失败时逆序回滚已初始化的模块，避免宿主启动失败后残留半启动状态。
/// </summary>
public sealed class ModuleManager : IModuleManager
{
    private readonly List<ModuleDescriptor> _modules;
    private IServiceProvider? _services;
    private bool _initialized;

    internal ModuleManager(IReadOnlyList<ModuleDescriptor> modules)
    {
        _modules = modules as List<ModuleDescriptor> ?? modules.ToList();
    }

    public IReadOnlyList<ModuleDescriptor> Modules => _modules;

    public bool IsInitialized => _initialized;

    public ModuleDescriptor GetModule<TModule>()
        where TModule : IModule => GetModule(typeof(TModule));

    public ModuleDescriptor GetModule(Type moduleType)
    {
        ArgumentNullException.ThrowIfNull(moduleType);

        return _modules.FirstOrDefault(descriptor => descriptor.ModuleType == moduleType)
            ?? throw new LedonModuleException($"未发现模块 {moduleType.FullName}。");
    }

    public async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (_initialized)
            return;

        _services = services;
        var initialized = new List<ModuleDescriptor>(_modules.Count);

        try
        {
            foreach (var descriptor in _modules)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await descriptor.Instance!.OnInitializedAsync(new ModuleContext(descriptor, services), cancellationToken);
                }
                catch
                {
                    descriptor.State = ModuleState.Failed;
                    throw;
                }

                descriptor.State = ModuleState.Initialized;
                initialized.Add(descriptor);
            }
        }
        catch
        {
            // 启动失败：把已经初始化的模块按逆序关闭，且不掩盖原始异常
            for (var i = initialized.Count - 1; i >= 0; i--)
            {
                var descriptor = initialized[i];
                try
                {
                    await ShutdownCoreAsync(descriptor, services, CancellationToken.None);
                    descriptor.State = ModuleState.Shutdown;
                }
                catch
                {
                    // 回滚过程中的次生异常仅保持状态，不替代抛出的原始异常
                }
            }

            throw;
        }

        _initialized = true;
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (!_initialized)
            return;

        var services = _services
            ?? throw new LedonModuleException("模块管理器状态异常：已初始化但服务容器丢失。");

        var errors = new List<Exception>();

        for (var i = _modules.Count - 1; i >= 0; i--)
        {
            var descriptor = _modules[i];
            if (descriptor.State != ModuleState.Initialized)
                continue;

            try
            {
                await ShutdownCoreAsync(descriptor, services, cancellationToken);
                descriptor.State = ModuleState.Shutdown;
            }
            catch (Exception ex)
            {
                descriptor.State = ModuleState.Failed;
                errors.Add(new LedonModuleException($"模块 {descriptor.Name} 关闭失败。", ex));
            }
        }

        _initialized = false;

        if (errors.Count == 1)
            ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1)
            throw new AggregateException("一个或多个模块关闭失败。", errors);
    }

    private static Task ShutdownCoreAsync(ModuleDescriptor descriptor, IServiceProvider services, CancellationToken cancellationToken) =>
        descriptor.Instance!.OnShutdownAsync(new ModuleContext(descriptor, services), cancellationToken);
}
