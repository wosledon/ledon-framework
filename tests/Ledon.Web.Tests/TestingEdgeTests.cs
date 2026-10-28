using Ledon.Module;
using Ledon.Module.Abstractions;
using Ledon.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>测试宿主边界：自定义管线、作用域、释放。</summary>
public class TestingEdgeTests
{
    [Fact]
    public async Task Host_starts_serves_requests_and_disposes()
    {
        await using var host = await LedonWebTestHost.StartAsync(configureApp: app =>
        {
            app.UseLedonWeb();
            app.MapGet("/ping", () => "pong");
        });

        Assert.Equal("pong", await host.Client.GetStringAsync("/ping"));
        Assert.NotNull(host.Services);
        Assert.NotNull(host.App);
    }

    [Fact]
    public async Task WithScope_creates_a_new_scope_each_call()
    {
        await using var host = await LedonWebTestHost.StartAsync(
            configure: builder => builder.Services.AddScoped<ScopedProbe>());

        var first = await host.WithScopeAsync(provider => Task.FromResult(provider.GetRequiredService<ScopedProbe>()));
        var second = await host.WithScopeAsync(provider => Task.FromResult(provider.GetRequiredService<ScopedProbe>()));

        Assert.NotSame(first, second);
    }

    [Fact]
    public void Module_assertions_reject_unknown_module()
    {
        using var provider = new ServiceCollection().AddLedon().BuildServiceProvider();
        var manager = provider.GetRequiredService<IModuleManager>();

        Assert.Throws<InvalidOperationException>(() => manager.ShouldBeInitialized("missing"));
    }

    [Fact]
    public void Module_assertions_reject_wrong_state_and_order()
    {
        using var provider = new ServiceCollection()
            .AddLedon(options => options
                .DisableAutoDiscovery()
                .AddModule<ModuleA>()
                .AddModule<ModuleB>())
            .BuildServiceProvider();
        var manager = provider.GetRequiredService<IModuleManager>();

        Assert.Throws<InvalidOperationException>(() => manager.ShouldBeInitialized("ModuleA"));
        Assert.Throws<InvalidOperationException>(() => manager.ShouldBeInitializedInOrder("ModuleB", "ModuleA"));
    }

    private sealed class ModuleA : IModule;

    private sealed class ModuleB : IModule;

    private sealed class ScopedProbe;
}
