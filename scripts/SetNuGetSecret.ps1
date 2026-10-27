param(
    [string]$Repo = "wosledon/ledon-framework",
    [string]$SecretName = "NUGET_API_KEY"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($env:NUGET_API_KEY)) {
    throw @"
未发现环境变量 NUGET_API_KEY。

请先在当前终端设置（不要把 Key 写进脚本/仓库）：
  `$env:NUGET_API_KEY = Read-Host -AsSecureString 'NuGet API Key' | ConvertFrom-SecureString -AsPlainText
然后运行：
  ./scripts/SetNuGetSecret.ps1
"@
}

gh auth status *> $null
if ($LASTEXITCODE -ne 0) {
    throw "GitHub CLI 尚未登录，请先运行：gh auth login"
}

Write-Host "正在写入 $Repo 的 Actions Secret：$SecretName"
$env:NUGET_API_KEY | gh secret set $SecretName --repo $Repo

if ($LASTEXITCODE -ne 0) {
    throw "写入 Secret 失败（exit=$LASTEXITCODE）"
}

Remove-Item Env:NUGET_API_KEY -ErrorAction SilentlyContinue

Write-Host "完成。仓库 Secret 列表："
gh secret list --repo $Repo | Select-String -Pattern $SecretName
