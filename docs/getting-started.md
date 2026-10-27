# 快速开始

## 环境要求

- .NET 10 SDK

## 用模板创建项目

```powershell
dotnet new install <仓库>/templates     # 安装模板（一次性）
dotnet new ledon-web -o MyApp           # Web 应用
dotnet new ledon-worker -o MyWorker     # 工作进程
dotnet new ledon-module -o MyApp.Module # 业务模块（独立程序集）
dotnet new ledon-react -o MyApp.React   # Ledon + React（Vite）+ 内置 RBAC
dotnet new ledon-blazor -o MyApp.Blazor # Ledon + Blazor Web App + 内置 RBAC
```

React 模板首次运行：

```powershell
cd MyApp.React/ClientApp && npm install && npm run dev   # 终端 1
cd .. && dotnet run                                       # 终端 2（http://localhost:5000）
```

Blazor 模板直接 `dotnet run`，打开 <http://localhost:5000>；两个模板都内置演示账号
`admin/admin123`（管理员）与 `user/user123`（reader）。
```

## 跑起来看

```powershell
cd MyApp
dotnet run    # 打开 /hello 与 /openapi/v1.json
```

## 运行仓库示例

```powershell
./run.ps1 web      # Web 示例（多程序集 + 全功能链路）
./run.ps1 worker   # 工作进程示例（跨程序集事件解耦）
./run.ps1 sample   # 控制台零配置示例
./run.ps1 capabilities  # 零依赖能力演示（ID/TOTP/密钥/配额/RBAC/CSV）
./run.ps1 rbac     # RBAC API 示例（JWT + RBAC + 幂等 + 配额）
./run.ps1 data     # 数据示例（SQLite + 多租户 + 播种 + CSV）
./run.ps1 config   # 配置中心示例（项目/环境/版本/权限/成员管理）
./run.ps1 check    # scan → build → test
```

## 第一个模块

```csharp
public class GreetingModule : IModule
{
    public Task OnInitializedAsync(ModuleContext ctx, CancellationToken ct = default)
    {
        ctx.Logger.LogInformation("GreetingModule 就绪");
        return Task.CompletedTask;
    }
}
```

宿主只要 `builder.AddLedonWeb()`，模块即被自动发现、按依赖序初始化——**无需任何注册代码**。
