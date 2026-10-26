using Ledon;
using Ledon.Data.Seeding;
using Ledon.Module;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 模块化数据播种入口（在 <c>AddLedonData()</c> 之后调用，确保晚于建库/迁移）：
/// <code>
/// builder.Services.AddLedonData();
/// builder.Services.AddLedonDataSeeding();
/// </code>
/// 实现 <see cref="IDataSeeder"/> 的类会被自动发现，按 <see cref="IDataSeeder.Order"/> 顺序执行。
/// </summary>
public static class LedonDataSeedingServiceCollectionExtensions
{
    public static IServiceCollection AddLedonDataSeeding(
        this IServiceCollection services,
        Action<DataSeedingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new DataSeedingOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);

        foreach (var type in LedonAssemblies.Scan()
            .SelectMany(assembly => assembly.GetLoadableTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false }
                && typeof(IDataSeeder).IsAssignableFrom(type)))
        {
            services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(IDataSeeder), type));
        }

        services.AddHostedService<DataSeedingHostedService>();

        return services;
    }
}

/// <summary>宿主启动时执行所有 <see cref="IDataSeeder"/>（按 Order 升序）。</summary>
internal sealed class DataSeedingHostedService(
    IServiceScopeFactory scopeFactory,
    DataSeedingOptions options,
    ILogger<DataSeedingHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled || !options.RunOnStartup)
            return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var seeders = scope.ServiceProvider.GetServices<IDataSeeder>()
            .OrderBy(seeder => seeder.Order)
            .ToArray();

        if (seeders.Length == 0)
            return;

        foreach (var seeder in seeders)
        {
            try
            {
                await seeder.SeedAsync(cancellationToken).ConfigureAwait(false);
                logger.LogInformation("数据播种完成：{Seeder}", seeder.GetType().Name);
            }
            catch (Exception ex) when (!options.FailFast)
            {
                logger.LogError(ex, "数据播种失败（FailFast = false，继续启动）：{Seeder}", seeder.GetType().Name);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
