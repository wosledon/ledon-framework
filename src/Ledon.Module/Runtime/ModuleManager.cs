using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Ledon.Module.Abstractions;
using Ledon.Module.Diagnostics;
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
    private readonly bool _parallelInitialization;
    private IServiceProvider? _services;
    private bool _initialized;

    internal ModuleManager(
        IReadOnlyList<ModuleDescriptor> modules,
        ModuleDiagnostics diagnostics,
        bool parallelInitialization = false)
    {
        _modules = modules as List<ModuleDescriptor> ?? modules.ToList();
        _diagnostics = diagnostics;
        _parallelInitialization = parallelInitialization;
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
            using (LedonActivity.Modules.StartActivity("PreInitialization"))
            {
                foreach (var task in services.GetServices<ILedonPreInitialization>())
                    await task.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            }

            // 依赖分层：同一层的模块互不依赖，可并行初始化（ParallelInitialization 开启时）；
            // 层与层之间保持拓扑序，被依赖者仍先初始化
            foreach (var level in DependencyLevels())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (level.Count == 1 || !_parallelInitialization)
                {
                    foreach (var descriptor in level)
                        await InitializeOneAsync(descriptor, services, initialized, cancellationToken).ConfigureAwait(false);

                    continue;
                }

                // Task.WhenAll 会等全部任务结束（即使其中一个失败）后再抛出首个异常，
                // 因此这里能拿到完整的 initialized 集合用于回滚
                await Task.WhenAll(level
                    .Select(descriptor => InitializeOneAsync(descriptor, services, initialized, cancellationToken)))
                    .ConfigureAwait(false);
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
                    await ShutdownCoreAsync(descriptor, services, CancellationToken.None).ConfigureAwait(false);
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

    private async Task InitializeOneAsync(
        ModuleDescriptor descriptor,
        IServiceProvider services,
        List<ModuleDescriptor> initialized,
        CancellationToken cancellationToken)
    {
        using var activity = LedonActivity.Modules.StartActivity("Module.Initialize");
        activity?.SetTag("module.name", descriptor.Name);
        activity?.SetTag("module.dependencies", string.Join(", ", descriptor.Dependencies.Select(d => d.Name)));

        try
        {
            await descriptor.Instance!.OnInitializedAsync(new ModuleContext(descriptor, services), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            descriptor.State = ModuleState.Failed;
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }

        descriptor.State = ModuleState.Initialized;

        lock (initialized)
            initialized.Add(descriptor);
    }

    /// <summary>
    /// 按依赖深度分层（层号 = 依赖链最长路径）：第 0 层无依赖，第 n 层只依赖更浅的层。
    /// 结果保持 <see cref="_modules"/> 内的相对顺序（拓扑序），并行执行时同层顺序不保证。
    /// </summary>
    private List<List<ModuleDescriptor>> DependencyLevels()
    {
        // Dependencies 里是模块 Type，先建 Type → 描述符映射
        var byType = _modules.ToDictionary(descriptor => descriptor.ModuleType);
        var levels = new Dictionary<ModuleDescriptor, int>(_modules.Count);

        int LevelOf(ModuleDescriptor descriptor)
        {
            if (levels.TryGetValue(descriptor, out var level))
                return level;

            var depth = 0;
            foreach (var dependency in descriptor.Dependencies)
            {
                if (!byType.TryGetValue(dependency, out var dependencyDescriptor))
                    continue;   // 依赖图构建时已校验存在，防御性跳过

                var dependencyLevel = LevelOf(dependencyDescriptor) + 1;
                if (dependencyLevel > depth)
                    depth = dependencyLevel;
            }

            levels[descriptor] = depth;
            return depth;
        }

        var groups = new List<List<ModuleDescriptor>>();
        foreach (var descriptor in _modules)
        {
            var level = LevelOf(descriptor);
            while (groups.Count <= level)
                groups.Add([]);

            groups[level].Add(descriptor);
        }

        return groups;
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

            using var activity = LedonActivity.Modules.StartActivity("Module.Shutdown");
            activity?.SetTag("module.name", descriptor.Name);

            try
            {
                await ShutdownCoreAsync(descriptor, services, cancellationToken).ConfigureAwait(false);
                descriptor.State = ModuleState.Shutdown;
            }
            catch (Exception ex)
            {
                descriptor.State = ModuleState.Failed;
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
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
