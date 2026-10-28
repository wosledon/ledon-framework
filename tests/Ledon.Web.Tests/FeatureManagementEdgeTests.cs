using Ledon.FeatureManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>功能开关边界：未知开关默认值、显式关闭优先级、空白名称。</summary>
public class FeatureManagementEdgeTests
{
    private static IFeatureManager Build(Dictionary<string, string?> config, bool defaultEnabled = false) =>
        new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(config).Build())
            .AddLedonFeatureManagement(options => options.DefaultEnabled = defaultEnabled)
            .BuildServiceProvider()
            .GetRequiredService<IFeatureManager>();

    [Fact]
    public void Unknown_feature_uses_configured_default()
    {
        var features = Build([], defaultEnabled: true);

        Assert.True(features.IsEnabled("never-registered"));
    }

    [Fact]
    public void Explicit_disabled_overrides_whitelist_boundary()
    {
        var features = Build(new Dictionary<string, string?>
        {
            ["Features:vip:Enabled"] = "false",
            ["Features:vip:Tenants:0"] = "t-001",
        });

        Assert.False(features.IsEnabled("vip", new FeatureTarget("t-001", null, null)));
    }

    [Fact]
    public void Whitespace_feature_name_is_rejected()
    {
        var features = Build([]);

        Assert.Throws<ArgumentException>(() => features.IsEnabled("   "));
    }
}
