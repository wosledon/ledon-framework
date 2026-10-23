using System.Diagnostics;

// 自动扫描项目内的 *.csproj 并用 `dotnet sln add` 添加到解决方案。
// 运行方式：dotnet run scripts/AutoCsproj.cs [要扫描的根目录]
// 若不传根目录，则扫描当前工作目录（建议在仓库根目录执行）。

var root = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Environment.CurrentDirectory;

if (!Directory.Exists(root))
{
    Console.Error.WriteLine($"目录不存在: {root}");
    return 1;
}

// 1. 定位解决方案文件（优先 .slnx，找不到再找 .sln）
var slnFiles = Directory.EnumerateFiles(root, "*.slnx")
    .Concat(Directory.EnumerateFiles(root, "*.sln"))
    .OrderBy(f => Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)
    .ToList();

if (slnFiles.Count == 0)
{
    Console.Error.WriteLine($"在 {root} 下未找到 .slnx / .sln 解决方案文件");
    return 1;
}

if (slnFiles.Count > 1)
{
    Console.Error.WriteLine("找到多个解决方案文件，请指定唯一的一个:");
    foreach (var f in slnFiles)
        Console.Error.WriteLine($"  {f}");
    return 1;
}

var sln = slnFiles[0];
Console.WriteLine($"解决方案: {Path.GetRelativePath(root, sln)}");

// 2. 递归扫描 csproj，排除 bin / obj 目录
var projects = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
    .Where(p => !IsUnderBuildOutput(root, p))
    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
    .ToList();

if (projects.Count == 0)
{
    Console.WriteLine("未找到任何 .csproj 文件。");
    return 0;
}

Console.WriteLine($"找到 {projects.Count} 个项目:");
foreach (var p in projects)
    Console.WriteLine($"  {Path.GetRelativePath(root, p)}");
Console.WriteLine();

// 3. 逐个执行 dotnet sln add
var failed = new List<string>();

foreach (var project in projects)
{
    var relative = Path.GetRelativePath(root, project);
    var (exitCode, output) = RunDotnetSln(root, sln, project);

    if (exitCode == 0)
    {
        Console.WriteLine($"[OK]   {relative}");
        if (!string.IsNullOrWhiteSpace(output))
            Console.WriteLine(Indent(output));
    }
    else
    {
        failed.Add(relative);
        Console.WriteLine($"[FAIL] {relative} (exit={exitCode})");
        if (!string.IsNullOrWhiteSpace(output))
            Console.WriteLine(Indent(output));
    }
}

Console.WriteLine();
if (failed.Count == 0)
{
    Console.WriteLine($"完成：{projects.Count} 个项目已添加到 {Path.GetFileName(sln)}");
    return 0;
}

Console.WriteLine($"完成：成功 {projects.Count - failed.Count}，失败 {failed.Count}");
foreach (var f in failed)
    Console.WriteLine($"  失败: {f}");
return 1;

// ---------- 局部函数 ----------

static bool IsUnderBuildOutput(string root, string path)
{
    var relative = Path.GetRelativePath(root, path);
    var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    return segments.Any(s =>
        s.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("obj", StringComparison.OrdinalIgnoreCase));
}

static (int ExitCode, string Output) RunDotnetSln(string workingDir, string slnFile, string projectFile)
{
    var psi = new ProcessStartInfo
    {
        FileName = "dotnet",
        WorkingDirectory = workingDir,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    psi.ArgumentList.Add("sln");
    psi.ArgumentList.Add(slnFile);
    psi.ArgumentList.Add("add");
    psi.ArgumentList.Add(projectFile);

    using var process = Process.Start(psi)
        ?? throw new InvalidOperationException("无法启动 dotnet 进程");

    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();

    var output = string.Join(Environment.NewLine, new[] { stdout.Trim(), stderr.Trim() }
        .Where(s => s.Length > 0));

    return (process.ExitCode, output);
}

static string Indent(string text) =>
    string.Join(Environment.NewLine,
        text.Split(Environment.NewLine).Select(line => "       " + line));
