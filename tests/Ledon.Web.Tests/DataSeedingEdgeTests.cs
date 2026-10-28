using Ledon.Data.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ledon.Web.Tests;

/// <summary>数据播种边界：停机、关闭自动执行、失败后继续。</summary>
public class DataSeedingEdgeTests : IAsyncLifetime
{
    public Task InitializeAsync()
    {
        SeedingRecorder.Entries.Clear();
        FailingSeeder.Fail = false;
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task StopAsync_does_not_throw_after_failure()
    {
        FailingSeeder.Fail = true;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedonDataSeeding(options => options.FailFast = false);

        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToArray();

        foreach (var service in hosted)
            await service.StartAsync(CancellationToken.None);

        foreach (var service in hosted)
            await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RunOnStartup_disabled_skips_seeders()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedonDataSeeding(options => options.RunOnStartup = false);

        await using var provider = services.BuildServiceProvider();

        foreach (var service in provider.GetServices<IHostedService>())
            await service.StartAsync(CancellationToken.None);

        Assert.Empty(SeedingRecorder.Entries);
    }
}
