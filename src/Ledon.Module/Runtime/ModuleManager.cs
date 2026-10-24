using System.Runtime.ExceptionServices;
using Ledon.Module.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ledon.Module.Runtime;

/// <summary>
/// <see cref="IModuleManager"/> 的默认实现：驱动初始化（拓扑序）与关闭（逆序）两个运行阶段，
/// 初始化失败时逆序回滚已初始化的模块，避免宿主启动失败后残留半启动状态。
/// </summary>
public sealed class ModuleManager : IModuleManager
{
    private readonly List<ModuleDescriptor> _modules;
    private readonly ModuleDiagnostics _diagnostics;
    private IServiceProvider? _services;
    private bool _initialized;

    internal ModuleManager(IReadOnlyList<ModuleDescriptor> modules, ModuleDiagnostics diagnostics)
    {
        _modules = modules as List<ModuleDescriptor> ?? modules.ToList();
        _diagnostics = diagnostics;
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
            LogDiagnostics(services);

            // 前置任务：先于任何模块初始化（如数据库建库/迁移），失败则整体中止
            foreach (var task in services.GetServices<ILedonPreInitialization>())
                await task.ExecuteAsync(cancellationToken);

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

    /// <summary>
    /// 输出启动诊断：Information 级别报告模块与扫描结果（回答"发现了什么"），
    /// Debug 级别给出跳过清单与约定注册明细（回答"为什么没发现/这个服务是谁注册的"）。
    /// </summary>
    private void LogDiagnostics(IServiceProvider services)
    {
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("Ledon.Module");
        if (logger is null)
            return;

        var moduleNames = string.Join(", ", _modules.Select(module => module.Name));
        logger.LogInformation(
            "模块发现完成：{ModuleCount} 个模块 [{Modules}]，扫描 {AssemblyCount} 个程序集 [{Assemblies}]",
            _modules.Count,
            moduleNames,
            _diagnostics.ScannedAssemblies.Count,
            string.Join(", ", _diagnostics.ScannedAssemblies));

        if (_diagnostics.SkippedAssemblies.Count > 0)
        {
            logger.LogDebug(
                "已跳过 {Count} 个已加载但未引用框架的程序集：{Skipped}",
                _diagnostics.SkippedAssemblies.Count,
                string.Join(", ", _diagnostics.SkippedAssemblies));
        }

        if (_diagnostics.ConventionRegistrations.Count > 0)
        {
            logger.LogDebug(
                "约定注册 {Count} 项：{Registrations}",
                _diagnostics.ConventionRegistrations.Count,
                string.Join(", ", _diagnostics.ConventionRegistrations));
        }
    }
}
