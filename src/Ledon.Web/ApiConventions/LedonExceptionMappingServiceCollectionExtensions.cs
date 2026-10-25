using Ledon.Web.ApiConventions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 异常映射登记：让业务包的异常（如 <c>Ledon.Application.ValidationException</c>）
/// 拿到正确的 HTTP 状态码，而 <c>Ledon.Web</c> 不必反向依赖业务包。
/// <code>
/// // 把 ValidationException 映射成 400（带逐条错误）
/// builder.Services.AddLedonExceptionMapping&lt;ValidationExceptionMapping&gt;();
/// </code>
/// 不登记时内置规则仍然生效（Argument → 400、未授权 → 403、找不到 → 404、其余 500）。
/// </summary>
public static class LedonExceptionMappingServiceCollectionExtensions
{
    /// <summary>登记一个异常映射（可多次调用；按登记顺序咨询，先匹配先赢）。</summary>
    public static IServiceCollection AddLedonExceptionMapping<TMapping>(this IServiceCollection services)
        where TMapping : class, IExceptionMapping
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IExceptionMapping, TMapping>());

        return services;
    }

    /// <summary>登记一个异常映射实例（映射需要外部状态时用）。</summary>
    public static IServiceCollection AddLedonExceptionMapping(
        this IServiceCollection services,
        IExceptionMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(mapping);

        services.TryAddEnumerable(ServiceDescriptor.Singleton(mapping));

        return services;
    }
}
