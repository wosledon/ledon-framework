using Ledon.Data.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ledon.Web.Tests;

/// <summary>数据播种：自动发现、Order 顺序、开关与失败策略。</summary>
public class DataSeedingTests : IAsyncLifetime
{
    public Task InitializeAsync()
    {
        SeedingRecorder.Entries.Clear();
        FailingSeeder.Fail = false;
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task RunHostedAsync(ServiceProvider provider)
    {
        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Seeders_are_discovered_and_run_in_order()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedonDataSeeding();

        await using var provider = services.BuildServiceProvider();
        await RunHostedAsync(provider);

        Assert.Equal(["first", "second"], SeedingRecorder.Entries);
    }

    [Fact]
    public async Task Disabled_seeding_does_not_run()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedonDataSeeding(o => o.Enabled = false);

        await using var provider = services.BuildServiceProvider();
        await RunHostedAsync(provider);

        Assert.Empty(SeedingRecorder.Entries);
    }

    [Fact]
    public async Task Failure_is_logged_and_swallowed_when_fail_fast_is_off()
    {
        FailingSeeder.Fail = true;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedonDataSeeding(o => o.FailFast = false);

        await using var provider = services.BuildServiceProvider();
        await RunHostedAsync(provider);

        Assert.Equal(["first", "second"], SeedingRecorder.Entries);
    }

    [Fact]
    public async Task Failure_stops_startup_when_fail_fast_is_on()
    {
        FailingSeeder.Fail = true;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedonDataSeeding(o => o.FailFast = true);

        await using var provider = services.BuildServiceProvider();

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunHostedAsync(provider));
    }
}

/// <summary>播种执行记录（测试间隔离）。</summary>
public static class SeedingRecorder
{
    public static List<string> Entries { get; } = [];
}

/// <summary>顺序 1。</summary>
public sealed class FirstSeeder : IDataSeeder
{
    public int Order => 1;

    public Task SeedAsync(CancellationToken cancellationToken = default)
    {
        SeedingRecorder.Entries.Add("first");
        return Task.CompletedTask;
    }
}

/// <summary>顺序 2。</summary>
public sealed class SecondSeeder : IDataSeeder
{
    public int Order => 2;

    public Task SeedAsync(CancellationToken cancellationToken = default)
    {
        SeedingRecorder.Entries.Add("second");
        return Task.CompletedTask;
    }
}

/// <summary>可控失败播种器。</summary>
public sealed class FailingSeeder : IDataSeeder
{
    public static bool Fail { get; set; }

    public int Order => 3;

    public Task SeedAsync(CancellationToken cancellationToken = default) =>
        Fail ? throw new InvalidOperationException("播种失败演示") : Task.CompletedTask;
}
