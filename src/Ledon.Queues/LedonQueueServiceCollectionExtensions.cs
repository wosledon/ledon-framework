using System.Threading.Channels;
using Ledon.Queues;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 后台队列入口扩展。日常用法一行：
/// <code>builder.Services.AddLedonQueues();</code>
/// 之后注入 <see cref="IBackgroundQueue"/> 即可丢任务。
/// </summary>
public static class LedonQueueServiceCollectionExtensions
{
    public static IServiceCollection AddLedonQueues(
        this IServiceCollection services,
        Action<LedonQueueOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 幂等
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IBackgroundQueue)))
            return services;

        var options = new LedonQueueOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        var channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(options.Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });

        services.AddSingleton(channel);
        services.TryAddSingleton<IBackgroundQueue, ChannelBackgroundQueue>();

        // 多 worker 并发处理：直接注册多个 IHostedService（AddHostedService 会去重，不能用）
        var workerCount = Math.Max(1, options.WorkerCount);
        for (var i = 0; i < workerCount; i++)
            services.Add(ServiceDescriptor.Singleton<IHostedService, BackgroundQueueWorker>());

        return services;
    }
}
