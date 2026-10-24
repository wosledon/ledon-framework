using System.Reflection;
using System.Text;
using Ledon.Module.Abstractions;

namespace Ledon.Module.Tests;

/// <summary>
/// 公开 API 基线：把 Ledon.Core / Ledon.Module 的公开类型与成员固化成文本基线，
/// 任何意外的公开面变更（删除/改名/签名变化）都会让测试失败。
/// 首次运行（无基线文件时）自动生成基线——生成后请 review 并提交。
/// </summary>
public class PublicApiBaselineTests
{
    private static string BaselinePath =>
        Path.Combine(AppContext.BaseDirectory, "PublicApi.baseline.txt");

    [Fact]
    public void Public_api_surface_matches_baseline()
    {
        var actual = string.Join(
            Environment.NewLine,
            DescribeAssembly(typeof(Ledon.Core.Events.IEventBus).Assembly),
            DescribeAssembly(typeof(IModule).Assembly));

        // 基线文件不存在或为空 = 重新生成（生成后复制到测试项目目录并提交）
        if (!File.Exists(BaselinePath) || new FileInfo(BaselinePath).Length == 0)
        {
            File.WriteAllText(BaselinePath, actual);
            return;
        }

        var baseline = File.ReadAllText(BaselinePath);
        Assert.Equal(
            baseline.ReplaceLineEndings(),
            actual.ReplaceLineEndings());
    }

    private static string DescribeAssembly(Assembly assembly)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {assembly.GetName().Name}");

        const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

        foreach (var type in assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            builder.AppendLine($"{KindOf(type)} {type.FullName}");

            foreach (var ctor in type.GetConstructors(Flags).OrderBy(m => m.ToString(), StringComparer.Ordinal))
                builder.AppendLine($"  .ctor({string.Join(", ", ctor.GetParameters().Select(p => p.ParameterType.Name))})");

            foreach (var method in type.GetMethods(Flags)
                .Where(method => !method.IsSpecialName)
                .OrderBy(method => method.ToString(), StringComparer.Ordinal))
            {
                builder.AppendLine($"  {method}");
            }

            foreach (var property in type.GetProperties(Flags).OrderBy(p => p.Name, StringComparer.Ordinal))
                builder.AppendLine($"  P {property.Name} : {property.PropertyType.Name}");

            foreach (var field in type.GetFields(Flags)
                .Where(field => !field.IsSpecialName)
                .OrderBy(field => field.Name, StringComparer.Ordinal))
            {
                builder.AppendLine($"  F {field.Name} : {field.FieldType.Name}");
            }
        }

        return builder.ToString();
    }

    private static string KindOf(Type type)
    {
        if (type.IsInterface) return "interface";
        if (type.IsEnum) return "enum";
        if (type.IsValueType) return "struct";
        if (type.IsSubclassOf(typeof(Delegate))) return "delegate";
        return "class";
    }
}
