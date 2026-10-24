using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace Ledon.Data;

/// <summary>
/// 连接弹性（瞬断重试）接入。<c>AddLedonData(o =&gt; o.EnableRetryOnFailure = true)</c>
/// 会自动为全部已注册 DbContext 调用本扩展；也可在自行配置 DbContext 时手动调用。
/// </summary>
public static class RelationalResilienceExtensions
{
    /// <summary>
    /// 为 DbContext 启用连接弹性：自动定位并调用数据库提供程序的
    /// <c>EnableRetryOnFailure</c> 扩展（SqlServer/Npgsql/MySql 等关系型提供程序各自实现，
    /// 签名为 (builder[, maxRetryCount[, maxDelay[, errorNumbers]]])）。
    /// 提供程序没有该扩展时静默跳过（如 SQLite 不支持瞬断重试；非关系型提供程序
    /// 自行负责连接弹性）——调用方无需判空。
    /// </summary>
    /// <param name="optionsBuilder">DbContext 选项构建器。</param>
    /// <param name="maxRetryCount">最大重试次数。</param>
    public static DbContextOptionsBuilder EnableRetryOnFailure(
        this DbContextOptionsBuilder optionsBuilder,
        int maxRetryCount = 3)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        var extensions = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(assembly => assembly.GetTypes()
                .Where(type => type is { IsSealed: true, IsAbstract: true, IsPublic: true }))
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(candidate => candidate is { Name: "EnableRetryOnFailure", IsGenericMethodDefinition: false }
                && candidate.GetParameters().Length >= 1
                && candidate.GetParameters()[0].ParameterType == typeof(DbContextOptionsBuilder))
            .ToList();

        // 优先 (builder, int maxRetryCount) 重载；否则退到无参重载
        var selected = extensions.FirstOrDefault(candidate =>
                candidate.GetParameters().Length == 2
                && candidate.GetParameters()[1].ParameterType == typeof(int))
            ?? extensions.FirstOrDefault(candidate => candidate.GetParameters().Length == 1);

        if (selected is null)
            return optionsBuilder;

        var arguments = selected.GetParameters()
            .Select(parameter => (object?)(parameter.Position switch
            {
                0 => optionsBuilder,
                1 when parameter.ParameterType == typeof(int) => maxRetryCount,
                _ => null,
            }))
            .ToArray();

        try
        {
            selected.Invoke(null, arguments);
        }
        catch (ArgumentException)
        {
            // 参数个数不匹配（提供程序签名不同）：换最少参数重载再试一次
            extensions.FirstOrDefault(candidate => candidate.GetParameters().Length == 1)
                ?.Invoke(null, [optionsBuilder]);
        }

        return optionsBuilder;
    }
}
