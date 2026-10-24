using System.Reflection;
using Xunit.Sdk;

namespace Ledon.Web.Tests;

/// <summary>
/// README 测试计数守护：README 声称的用例数必须与实际 [Fact]/[Theory] 数量一致，
/// 防止文档悄悄漂移（此前靠手工维护，已经漂移过）。新增用例后更新 README 数字即可。
/// </summary>
public class ReadmeTestCountTests
{
    [Fact]
    public void Readme_web_test_count_matches_actual_fact_count()
    {
        var actual = typeof(ReadmeTestCountTests).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Count(method => method.IsDefined(typeof(FactAttribute), inherit: true)
                || method.IsDefined(typeof(TheoryAttribute), inherit: true));

        var readme = File.ReadAllText(FindReadme());
        var match = System.Text.RegularExpressions.Regex.Match(readme, @"Web\s*(\d+)");

        Assert.True(match.Success, "README 中未找到“Web N”的测试计数");
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
