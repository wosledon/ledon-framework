# 快速调用 scripts/Run.cs 任务编排器
# 用法（仓库根目录）：
#   ./run.ps1                # 列出所有任务
#   ./run.ps1 check          # 执行流水线
#   ./run.ps1 build --no-restore   # 额外参数透传
# 也可在任意子目录调用（会自动切回仓库根）

param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$TaskArgs = @()
)

$ErrorActionPreference = 'Stop'

Push-Location $PSScriptRoot
try {
    & dotnet run scripts/Run.cs -- @TaskArgs
    $exitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}

exit $exitCode
