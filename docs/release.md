# 发布与 NuGet

## 版本策略

- 包版本来自 `Directory.Build.props` 的 `VersionPrefix`（当前 `0.1.0`）；
- 打 `v*` tag 时，CI 用 tag 覆盖版本：`v0.2.0` → `0.2.0`；
- 所有 `IsPackable=false` 的示例/测试项目不会打包。

## 配置 NuGet Key

不要把 NuGet API Key 提交到仓库或发在聊天里。推荐使用 GitHub Actions Secret：

### 网页

仓库 → Settings → Secrets and variables → Actions → New repository secret

- Name：`NUGET_API_KEY`
- Value：nuget.org 创建的 API Key

### 命令行（安全输入）

```powershell
gh auth login
$secure = Read-Host -AsSecureString "NuGet API Key"
$env:NUGET_API_KEY = ConvertFrom-SecureString -SecureString $secure -AsPlainText
./scripts/SetNuGetSecret.ps1
Remove-Item Env:NUGET_API_KEY
Remove-Variable secure
```

`SetNuGetSecret.ps1` 通过标准输入把值交给 `gh secret set`，不会把 Key 放进命令行参数。

## 发布

```powershell
./run.ps1 pack     # 本地打包到 artifacts/packages
git tag v0.2.0
git push origin v0.2.0
```

tag 触发 `.github/workflows/ci.yml` 的 `pack` job：

1. 校验 `NUGET_API_KEY` 是否存在；
2. `dotnet pack Ledon.slnx -c Release -p:Version=0.2.0 -o out`；
3. `dotnet nuget push out/*.nupkg --skip-duplicate` 到 nuget.org。

查看：

```powershell
gh run list --repo wosledon/ledon-framework
gh run watch --repo wosledon/ledon-framework
```

## Key 权限建议

- Scope：Push new packages and package versions；
- Package pattern：`Ledon.*`；
- 设置过期时间；
- Key 泄露立即在 nuget.org 撤销并轮换 Secret。
