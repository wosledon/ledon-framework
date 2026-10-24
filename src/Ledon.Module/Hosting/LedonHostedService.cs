using Ledon.Module.Abstractions;
using Microsoft.Extensions.Hosting;

namespace Ledon.Module.Hosting;

/// <summary>
/// 把模块生命周期接入通用主机：<c>host.StartAsync</c> → 初始化模块，<c>host.StopAsync</c> → 关闭模块。
/// 不使用通用主机的场景可改用 <c>serviceProvider.InitializeModulesAsync()</c>。
/// </summary>
internal sealed class LedonHostedService : IHostedService
{
    private readonly IModuleManager _manager;
    private readonly IServiceProvider _services;

    public LedonHostedService(IModuleManager manager, IServiceProvider services)
    {
        _manager = manager;
        _services = services;
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        _manager.InitializeAsync(_services, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        _manager.ShutdownAsync(cancellationToken);
}
