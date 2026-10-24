using System.Reflection;
using Ledon.Core.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ledon.Module;

/// <summary>
/// 约定扫描自动注册：把程序集中的类按约定批量注册进服务容器。
/// 每个具体类依次应用以下规则（全部使用 <c>TryAdd</c>，已手动注册的不会被覆盖）：
/// <list type="number">
///   <item>存在与类同名的接口（<c>IName</c> ↔ <c>Name</c>，忽略泛型 arity）→ 注册这些接口，使用指定生命周期；</item>
///   <item>实现了 <see cref="IEventHandler{TEvent}"/> → 以瞬时生命周期注册这些处理器接口（保证每次发布新建实例）。</item>
/// </list>
/// 没有接口的类（DTO、控制器、端点等）**不会**被自动注册——这不仅是减少噪音：
/// 把 MVC 动作参数类型注册进容器会让 <c>[ApiController]</c> 的隐式 <c>[FromBody]</c> 推断失效
/// （实测：注册即退化为查询字符串绑定）。此类类型请显式注册。
/// </summary>
public static class ConventionScanner
{
    /// <summary>扫描 <paramref name="assembly"/> 并按约定注册服务。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="assembly">要扫描的程序集。</param>
    /// <param name="lifetime">无接口类/同名接口类的默认生命周期，默认 <see cref="ServiceLifetime.Scoped"/>。</param>
    /// <param name="log">可选的诊断收集器：记录每条约定注册，便于回答"这个服务是谁注册的"。</param>
    public static IServiceCollection ScanAssembly(
        this IServiceCollection services,
        Assembly assembly,
        ServiceLifetime lifetime = ServiceLifetime.Scoped,
        ICollection<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assembly);

        foreach (var type in assembly.GetLoadableTypes().Where(IsConcreteClass))
        {
            var serviceInterfaces = type.GetInterfaces()
                .Where(serviceInterface => MatchesServiceName(serviceInterface, type))
                .ToList();

            var handlerInterfaces = type.GetInterfaces()
                .Where(serviceInterface => serviceInterface.IsGenericType
                    && serviceInterface.GetGenericTypeDefinition() == typeof(IEventHandler<>))
                .ToList();

            foreach (var serviceInterface in serviceInterfaces)
            {
                services.TryAdd(new ServiceDescriptor(serviceInterface, type, lifetime));
                log?.Add($"{serviceInterface.Name} -> {type.Name} ({lifetime})");
            }

            foreach (var handlerInterface in handlerInterfaces)
            {
                services.TryAdd(new ServiceDescriptor(handlerInterface, type, ServiceLifetime.Transient));
                log?.Add($"{handlerInterface.Name} -> {type.Name} (Transient)");
            }
        }

        return services;
    }

    private static bool IsConcreteClass(Type type) =>
        type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
        && type.Name.IndexOf('<') < 0
        && type.Name.IndexOf('>') < 0
        && !type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false);

    /// <summary>接口名是否等于 "I" + 类名（忽略 `1、`2 等泛型 arity 后缀）。</summary>
    private static bool MatchesServiceName(Type serviceInterface, Type implementation) =>
        StripArity(serviceInterface.Name) == $"I{StripArity(implementation.Name)}";

    private static string StripArity(string name)
    {
        var backtick = name.IndexOf('`');
        return backtick < 0 ? name : name[..backtick];
    }
}
