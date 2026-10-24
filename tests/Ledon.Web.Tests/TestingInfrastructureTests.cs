using Ledon.Module;
using Ledon.Module.Abstractions;
using Ledon.Testing;
using Ledon.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>Ledon.Testing 基建自证：用测试宿主 + 断言助手完成一次完整验证。</summary>
public class TestingInfrastructureTests
{
    [Fact]
    public async Task Ledon_web_test_host_simplifies_integration_tests()
    {
        await using var host = await LedonWebTestHost.StartAsync(builder =>
        {
            builder.AddLedonWeb();
            builder.Services.AddLedon(o => o.DisableAutoDiscovery().AddModule<Fixtures.NoopModule>());
        });

        // 请求
        Assert.Equal("world", await host.Client.GetStringAsync("/hello"));

        // 模块生命周期断言
        var manager = host.Services.GetRequiredService<IModuleManager>();
        manager.ShouldBeInitialized("NoopModule");
    }

    [Fact]
    public async Task With_scope_helper_resolves_scoped_services()
    {
        await using var host = await LedonWebTestHost.StartAsync(builder =>
        {
            builder.AddLedonWeb();
            builder.Services.AddScoped<Fixtures.Payload>();
        });

        var payload = await host.WithScopeAsync(services => Task.FromResult(services.GetRequiredService<Fixtures.Payload>()));

        Assert.NotNull(payload);
    }
}
