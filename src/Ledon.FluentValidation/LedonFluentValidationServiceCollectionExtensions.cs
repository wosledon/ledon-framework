using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using FluentValidation;
using FluentValidation.AspNetCore;
using Ledon;
using Ledon.Module;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// FluentValidation 集成：自动发现被扫描程序集中的 <c>AbstractValidator&lt;T&gt;</c> 实现并注册，
/// 经 MVC 的模型验证管线统一产出 400 ValidationProblemDetails（与 DataAnnotations 同一出口）。
/// <code>
/// builder.Services.AddLedonFluentValidation();
/// // public class EchoRequestValidator : AbstractValidator&lt;EchoRequest&gt; { ... }
/// </code>
/// 最小 API 场景改用 <c>AddFluentValidationAutoValidation()</c> 的标准用法即可。
/// </summary>
public static class LedonFluentValidationServiceCollectionExtensions
{
    /// <summary>反射边界说明（裁剪/NativeAOT 警告文案）。</summary>
    private const string AotBoundary =
        "Ledon 的验证器自动发现基于反射（Assembly.GetTypes）。" +
        "NativeAOT 或裁剪发布时，验证器类型必须可在运行时解析（rd.xml / DynamicDependency 保留）。";

    [RequiresDynamicCode(AotBoundary)]
    public static IServiceCollection AddLedonFluentValidation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 自动发现验证器（沿用框架的允许列表扫描）；已手动注册的不覆盖
        var validatorInterface = typeof(IValidator<>);
        foreach (var assembly in LedonAssemblies.Scan())
        {
            foreach (var type in assembly.GetLoadableTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false }
                    && type.GetInterfaces().Any(i =>
                        i.IsGenericType && i.GetGenericTypeDefinition() == validatorInterface)))
            {
                foreach (var implemented in type.GetInterfaces()
                    .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == validatorInterface))
                {
                    services.TryAddEnumerable(ServiceDescriptor.Scoped(implemented, type));
                }
            }
        }

        // MVC 模型验证管线接入（ApiBehaviorOptions 的 InvalidModelStateResponseFactory 已由
        // AddLedonWeb 统一配成 400 ValidationProblemDetails，这里只负责把 FV 结果汇入 ModelState）
        services.AddFluentValidationAutoValidation();

        return services;
    }
}
