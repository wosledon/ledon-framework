using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Ledon;
using Ledon.Module;
using Ledon.Module.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ledon.Data;

/// <summary>
/// 数据层入口。日常用法一行：
/// <code>builder.Services.AddLedonData();</code>
/// 自动完成：DbContext 发现与注册、建库/迁移（作为前置任务先于模块初始化）。
/// </summary>
public static class LedonDataServiceCollectionExtensions
{
    /// <summary>反射边界说明（裁剪/NativeAOT 警告文案）。</summary>
    private const string AotBoundary =
        "Ledon 的自动发现与约定注入基于反射（Assembly.GetTypes / Activator.CreateInstance）。" +
        "NativeAOT 或裁剪发布时，DbContext 与业务类型必须可在运行时解析（rd.xml / DynamicDependency 保留）。";

    [RequiresDynamicCode(AotBoundary)]
    public static IServiceCollection AddLedonData(
        this IServiceCollection services,
        Action<LedonDataOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 幂等确保模块系统在场（数据层复用其扫描与生命周期）
        services.AddLedon();

        var options = new LedonDataOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        foreach (var assembly in LedonAssemblies.Scan())
        {
            foreach (var type in assembly.GetLoadableTypes().Where(IsDbContext))
            {
                RegisterDbContext(services, type);
                services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(ILedonDbContext), type));
            }
        }

        // 审计拦截器（时间戳/操作人自动填充 + 软删除转换）：实现标记接口即生效，
        // 具体行为由 LedonDataOptions 控制（EnableAuditing / AutoSoftDelete）。
        // 经 IDbContextOptionsConfiguration<> 接入全部已注册的 DbContext
        // （用户自己 AddDbContext 配连接串也生效；EF 不会自动发现 DI 里的拦截器）
        services.TryAddSingleton<LedonAuditInterceptor>();

        // 领域事件分发（SaveChanges 成功后经 IEventBus 分发 IHasDomainEvents 实体上的事件）。
        // 关闭时注册 null 占位（非泛型重载），让同一个 OptionsConfiguration 的条件注入保持简单
        if (options.DispatchDomainEvents)
            services.TryAddSingleton<DomainEventsDispatcherInterceptor>();
        else
            services.TryAdd(ServiceDescriptor.Singleton(
                typeof(DomainEventsDispatcherInterceptor), _ => null!));

        services.TryAdd(ServiceDescriptor.Singleton(
            typeof(IDbContextOptionsConfiguration<>),
            typeof(LedonAuditOptionsConfiguration<>)));

        // 通用仓储（按实体注册 IRepository<T>）
        if (options.UseRepositories)
            services.AddLedonRepositories();

        // 建库/迁移是前置任务：先于任何模块初始化执行，模块里可安全使用数据库
        services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(ILedonPreInitialization), typeof(DbInitializer)));

        // 数据库连通性健康检查（就绪探针用）：未接健康检查端点时是惰性登记，无副作用
        services.TryAddSingleton<LedonDbContextHealthCheck>();
        services.AddHealthChecks().AddCheck<LedonDbContextHealthCheck>("database");

        return services;
    }

    private static bool IsDbContext(Type type) =>
        type is { IsClass: true, IsAbstract: false }
        && type != typeof(DbContext)
        // ILedonDbContext 是选择加入的标记：只登记实现了它的 DbContext，
        // 否则任意 DbContext（如继承 OutboxDbContext 的）会被误注册为 ILedonDbContext
        && typeof(ILedonDbContext).IsAssignableFrom(type)
        // [DisableAutoDiscovery] 逃生门：测试夹具/仅手动注册的上下文不参与自动发现
        && !type.IsDefined(typeof(DisableAutoDiscoveryAttribute), inherit: false);

    /// <summary>
    /// 通过反射调用 EF 的 <c>AddDbContext</c>（DbContext 类型是运行时发现的）。
    /// EF 各版本的重载签名有差异，这里按参数逐个映射填值，兼容多版本。
    /// </summary>
    private static void RegisterDbContext(IServiceCollection services, Type contextType)
    {
        var candidates = typeof(EntityFrameworkServiceCollectionExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method is { Name: nameof(EntityFrameworkServiceCollectionExtensions.AddDbContext) })
            .ToList();

        // 优先非泛重重载（直接收 Type）
        var nonGeneric = candidates.FirstOrDefault(method => !method.IsGenericMethodDefinition
            && method.GetParameters().Any(parameter => parameter.ParameterType == typeof(Type)));

        var generic = candidates
            .Where(method => method.IsGenericMethodDefinition)
            .OrderBy(method => method.GetParameters().Length)
            .FirstOrDefault();

        var method = nonGeneric ?? generic
            ?? throw new LedonModuleException("未找到 EntityFrameworkServiceCollectionExtensions.AddDbContext，请检查 EF Core 版本。");

        var closed = method.IsGenericMethodDefinition
            ? method.MakeGenericMethod(contextType)
            : method;

        object? MapArgument(ParameterInfo parameter) => parameter.ParameterType switch
        {
            _ when parameter.ParameterType == typeof(IServiceCollection) => services,
            _ when parameter.ParameterType == typeof(Type) => contextType,
            _ when parameter.ParameterType == typeof(ServiceLifetime) => ServiceLifetime.Scoped,
            _ => null,
        };

        var arguments = closed.GetParameters().Select(MapArgument).ToArray();
        closed.Invoke(null, arguments);
    }
}
