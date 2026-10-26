using Xunit;

namespace Ledon.Web.Tests;

/// <summary>依赖许可证守护：禁止引入付费/商业授权组件。</summary>
public class DependencyLicenseTests
{
    private static readonly string[] BannedPackages =
    [
        "ImageSharp",
        "QuestPDF",
        "Aspose",
        "Syncfusion",
        "Telerik",
        "DevExpress",
        "IronSoftware",
        "Xceed",
        "Grapecity",
    ];

    [Fact]
    public void Project_files_do_not_reference_commercial_packages()
    {
        var root = FindRepositoryRoot();
        var files = Directory
            .EnumerateFiles(root, "*.*proj", SearchOption.AllDirectories)
            .Append(Path.Combine(root, "Directory.Packages.props"))
            .Where(File.Exists);

        var violations = new List<string>();

        foreach (var file in files)
        {
            var content = File.ReadAllText(file);

            foreach (var package in BannedPackages)
            {
                if (content.Contains(package, StringComparison.OrdinalIgnoreCase))
                    violations.Add($"{Path.GetRelativePath(root, file)}: {package}");
            }
        }

        Assert.True(violations.Count == 0, "发现商业/付费组件：" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Ledon.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("未找到仓库根目录（Ledon.slnx）");
    }
}
