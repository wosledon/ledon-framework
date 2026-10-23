using System.Diagnostics;

// 任务别名 / 命令编排（自制 makefile / npm scripts）
//
// 用法：
//   dotnet run scripts/Run.cs                      # 列出所有任务
//   dotnet run scripts/Run.cs <task> [args...]     # 执行任务，额外参数透传给该任务的命令
//
// 扩展方式：在下方 tasks 字典里加一行即可。
//   Command：任务要执行的 shell 命令（pwsh 执行，可用 &&、| 等语法）
//   Deps   ：执行本任务前必须先成功的前置任务（会自动去重、防环）
//   纯聚合任务可以只写 Deps 不写 Command（如 check）。

var root = Environment.CurrentDirectory;

var tasks = new Dictionary<string, TaskDef>(StringComparer.OrdinalIgnoreCase)
{
    ["scan"] = new("扫描 csproj 并添加到 slnx", "dotnet run scripts/AutoCsproj.cs"),
    ["build"] = new("构建解决方案", "dotnet build Ledon.slnx"),
    ["test"] = new("运行测试", "dotnet test Ledon.slnx"),
    ["check"] = new("流水线：scan → build → test", Deps: ["scan", "build", "test"]),
};

// ---------- 列出任务 ----------
if (args.Length == 0 || args[0] is "-h" or "--help" or "help" or "list")
{
    Console.WriteLine("可用任务：");
    foreach (var (name, def) in tasks)
    {
        var deps = def.Deps is { Length: > 0 } ? $"  [依赖: {string.Join(", ", def.Deps)}]" : "";
        Console.WriteLine($"  {name,-7} {def.Desc}{deps}");
    }
    Console.WriteLine();
    Console.WriteLine("执行：dotnet run scripts/Run.cs <task> [args...]");
    return 0;
}

// ---------- 解析任务 ----------
var target = args[0];
if (!tasks.TryGetValue(target, out var targetDef))
{
    Console.Error.WriteLine($"未知任务: {target}");
    Console.Error.WriteLine($"可用任务: {string.Join(", ", tasks.Keys)}");
    return 1;
}

if (targetDef.Command is null && targetDef.Deps is null)
{
    Console.Error.WriteLine($"任务 {target} 没有可执行的命令");
    return 1;
}

var extraArgs = args.Skip(1).ToArray();
var executed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var failed = new List<string>();
var rootTask = target;

// ---------- 执行（带依赖，失败即停） ----------
int Execute(string name)
{
    if (!tasks.TryGetValue(name, out var def))
    {
        Console.Error.WriteLine($"未知的前置任务: {name}");
        return 1;
    }

    if (!executed.Add(name))
        return 0; // 已执行过（去重），依赖图天然防环

    foreach (var dep in def.Deps ?? [])
    {
        if (Execute(dep) != 0)
            return 1;
    }

    var command = def.Command;
    if (command is null)
    {
        // 纯聚合任务没有命令，透传参数无处可去，明确提示而不是静默丢弃
        if (extraArgs.Length > 0 && name == rootTask)
            Console.WriteLine($"--- [{name}] 提示：聚合任务不接受参数，已忽略: {string.Join(' ', extraArgs)}");
        return 0;
    }

    // 额外参数只透传给顶层任务，不污染前置任务
    if (extraArgs.Length > 0 && name == rootTask)
        command = $"{command} {string.Join(' ', extraArgs)}";

    Console.WriteLine();
    Console.WriteLine($"=== [{name}] {command}");

    var psi = new ProcessStartInfo
    {
        FileName = "pwsh",
        WorkingDirectory = root,
        UseShellExecute = false,
    };
    psi.ArgumentList.Add("-NoProfile");
    psi.ArgumentList.Add("-NonInteractive");
    psi.ArgumentList.Add("-Command");
    psi.ArgumentList.Add(command);

    using var process = Process.Start(psi);
    if (process is null)
    {
        Console.Error.WriteLine($"无法启动命令: {command}");
        failed.Add(name);
        return 1;
    }

    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        Console.WriteLine($"--- [{name}] 失败 (exit={process.ExitCode})，中止后续任务");
        failed.Add(name);
        return 1;
    }

    Console.WriteLine($"--- [{name}] 完成");
    return 0;
}

var exitCode = Execute(target);

Console.WriteLine();
if (exitCode == 0)
{
    Console.WriteLine($"任务 {target} 成功（已执行: {string.Join(" → ", executed)}）");
}
else
{
    Console.WriteLine($"任务 {target} 失败（失败任务: {string.Join(", ", failed)}）");
}

return exitCode;

// ---------- 类型定义 ----------

record TaskDef(string Desc, string? Command = null, string[]? Deps = null);
