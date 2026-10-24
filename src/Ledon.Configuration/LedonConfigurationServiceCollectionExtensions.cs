using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 强类型配置入口。约定：
/// <list type="bullet">
///   <item>节名默认取类型名去掉 "Options" 后缀（<c>SmtpOptions</c> → 节 <c>Smtp</c>）；</item>
///   <item>自动绑定配置节 + DataAnnotations 校验 + <c>ValidateOnStart</c>（配错了启动即失败，不拖到运行时）；</item>
///   <item>实现 <see cref="IValidatableObject"/> 的选项同样在启动时校验。</item>
/// </list>
/// <code>
/// builder.Services.AddLedonOptions&lt;SmtpOptions&gt;(builder.Configuration);
/// // appsettings.json: { "Smtp": { "Host": "...", "Port": 587 } }
/// </code>
/// </summary>
public static class LedonConfigurationServiceCollectionExtensions
{
    public static IServiceCollection AddLedonOptions<TOptions>(
        this IServiceCollection services,
        IConfiguration configuration,
        string? sectionName = null)
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // 节名约定：类型名去掉 Options 后缀（带前缀时由调用方显式指定）
        var section = sectionName ?? typeof(TOptions).Name;
        if (section.EndsWith("Options", StringComparison.Ordinal))
            section = section[..^"Options".Length];

        var builder = services
            .AddOptions<TOptions>()
            .Bind(configuration.GetSection(section))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // IValidatableObject 启动校验（ValidateOnStart 不覆盖它）
        return builder.Validate(
            options =>
            {
                if (options is not IValidatableObject validatable)
                    return true;

                return validatable
                    .Validate(new ValidationContext(options))
                    .All(result => result == ValidationResult.Success);
            },
            "选项校验失败").Services;
    }
}
