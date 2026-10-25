using Ledon.FeatureManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>功能开关：fail-closed、白名单、百分比灰度、运行时覆盖。</summary>
public class FeatureManagementTests
{
    private static IFeatureManager Build(Dictionary<string, string?> config, bool defaultEnabled = false) =>
        new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(config).Build())
            .AddLedonFeatureManagement(o => o.DefaultEnabled = defaultEnabled)
            .BuildServiceProvider()
            .GetRequiredService<IFeatureManager>();

    [Fact]
    public void Unregistered_feature_is_closed_by_default()
    {
        var features = Build([]);

        Assert.False(features.IsEnabled("not-registered"));
    }

    [Fact]
    public void Boolean_feature_reads_from_configuration()
    {
        var features = Build(new Dictionary<string, string?>
        {
            ["Features:new-checkout"] = "true",
            ["Features:legacy"] = "false",
        });

        Assert.True(features.IsEnabled("new-checkout"));
        Assert.False(features.IsEnabled("legacy"));
    }

    [Fact]
    public void Tenant_whitelist_only_opens_for_listed_tenants()
    {
        var features = Build(new Dictionary<string, string?>
        {
            ["Features:vip-only:Tenants:0"] = "t-001",
        });

        Assert.True(features.IsEnabled("vip-only", new FeatureTarget("t-001", null, null)));
        Assert.False(features.IsEnabled("vip-only", new FeatureTarget("t-002", null, null)));
    }

    [Fact]
    public void Percentage_is_stable_per_subject_and_honors_bounds()
    {
        var off = Build(new Dictionary<string, string?> { ["Features:beta:Percentage"] = "0" });
        var on = Build(new Dictionary<string, string?> { ["Features:beta:Percentage"] = "100" });

        Assert.False(off.IsEnabled("beta", new FeatureTarget(null, "u-1", null)));
        Assert.True(on.IsEnabled("beta", new FeatureTarget(null, "u-1", null)));
    }

    [Fact]
    public void Percentage_bucket_is_deterministic()
    {
        var features = Build(new Dictionary<string, string?> { ["Features:beta:Percentage"] = "50" });

        var first = features.IsEnabled("beta", new FeatureTarget(null, "u-42", null));

        // 同一主体判定必须稳定（不能一会儿开一会儿关）
        for (var i = 0; i < 20; i++)
            Assert.Equal(first, features.IsEnabled("beta", new FeatureTarget(null, "u-42", null)));
    }

    [Fact]
    public void Runtime_override_wins_over_configuration()
    {
        var features = Build(new Dictionary<string, string?> { ["Features:beta"] = "false" });

        features.SetEnabled("beta", true);
        Assert.True(features.IsEnabled("beta"));

        features.SetEnabled("beta", false);
        Assert.False(features.IsEnabled("beta"));
    }

    [Fact]
    public void Features_lists_registered_names()
    {
        var features = Build(new Dictionary<string, string?>
        {
            ["Features:a"] = "true",
            ["Features:b"] = "false",
        });

        Assert.Equal(["a", "b"], features.Features.Order());
    }

    [Fact]
    public async Task Async_api_matches_sync()
    {
        var features = Build(new Dictionary<string, string?> { ["Features:x"] = "true" });

        Assert.True(await features.IsEnabledAsync("x"));
        Assert.False(await features.IsEnabledAsync("y"));
    }
}
