using System.Reflection;
using Xunit.Sdk;

namespace Ledon.Module.Tests;

/// <summary>
/// README 测试计数守护（模块测试项目一侧）：README 声称的模块用例数必须与实际一致。
/// 与 Web 测试项目的同名守护各管一侧，两侧数字之和即 README 的测试总数。
/// </summary>
public class ReadmeTestCountTests
{
    [Fact]
    public void Readme_module_test_count_matches_actual_fact_count()
    {
        var actual = typeof(ReadmeTestCountTests).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Count(method => method.IsDefined(typeof(FactAttribute), inherit: true)
                || method.IsDefined(typeof(TheoryAttribute), inherit: true));

        var readme = File.ReadAllText(FindReadme());
        var match = System.Text.RegularExpressions.Regex.Match(readme, @"模块\s*(\d+)");

        Assert.True(match.Success, "README 中未找到“模块 N”的测试计数");
        Assert.Equal(int.Parse(match.Groups[1].Value), actual);
    }

    private static string FindReadme()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "README.md");
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("未找到 README.md");
    }
}
