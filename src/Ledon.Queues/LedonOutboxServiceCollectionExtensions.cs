using System.Diagnostics.CodeAnalysis;
using Ledon;
using Ledon.Module;
using Ledon.Queues;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 发件箱入口。日常用法：
/// <code>
/// builder.Services.AddLedonOutbox();
/// // 业务代码：outbox.Add(new OrderPlaced(orderId)); await db.SaveChangesAsync();
/// </code>
/// DbContext 继承 <see cref="OutboxDbContext"/>，调度器按轮询间隔投递（至少一次）。
/// </summary>
public static class LedonOutboxServiceCollectionExtensions
{
    /// <summary>反射边界说明（裁剪/NativeAOT 警告文案）。</summary>
    private const string AotBoundary =
        "Ledon 的自动发现与约定注入基于反射（Assembly.GetTypes / Type.GetType 反序列化）。" +
        "NativeAOT 或裁剪发布时，集成事件类型必须可在运行时解析（rd.xml / DynamicDependency 保留）。";

    [RequiresDynamicCode(AotBoundary)]
    public static IServiceCollection AddLedonOutbox(
        this IServiceCollection services,
        Action<LedonOutboxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new LedonOutboxOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        // 发件箱存储（EF 实现，工作在与用户 DbContext 相同的注册上）
        services.TryAddScoped<IOutboxStore, EfOutboxStore>();

        // OutboxDbContext 子类自动发现：把具体类型注册为基类型（EfOutboxStore 依赖基类型解析）。
        // 用户仍需自行 AddDbContext<T>() 注册连接等配置。
        foreach (var assembly in LedonAssemblies.Scan())
        {
            foreach (var type in assembly.GetLoadableTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false }
                    && type != typeof(OutboxDbContext)
                    && typeof(OutboxDbContext).IsAssignableFrom(type)))
            {
                services.AddScoped(typeof(OutboxDbContext), provider => provider.GetRequiredService(type));
            }
        }

        // 集成事件处理器自动发现（沿用框架的允许列表扫描）
        foreach (var assembly in LedonAssemblies.Scan())
        {
            foreach (var type in assembly.GetLoadableTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false }
                    && type.GetInterfaces().Any(service => service.IsGenericType
                        && service.GetGenericTypeDefinition() == typeof(IIntegrationEventHandler<>))))
            {
                foreach (var service in type.GetInterfaces()
                    .Where(service => service.IsGenericType
                        && service.GetGenericTypeDefinition() == typeof(IIntegrationEventHandler<>)))
                {
                    services.TryAddEnumerable(ServiceDescriptor.Scoped(service, type));
                }
            }
        }

        services.AddHostedService<OutboxDispatcher>();

        return services;
    }
}
