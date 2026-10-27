# Ledon Blazor App

Ledon Framework + Blazor Web App（Interactive Server）模板，内置可直接使用的 RBAC。

## 启动

```bash
dotnet run
```

打开 <http://localhost:5000>，使用演示账号登录。

## 演示账号

| 账号 | 密码 | 角色/权限 |
|---|---|---|
| `admin` | `admin123` | 内置 `admin` 角色，权限 `*` |
| `user` | `user123` | `reader`，权限 `weather.read` |

## 内置 RBAC

- 登录调用 `/auth/login` 获取 JWT；
- `/auth/authorization/me` 展示当前角色与权限；
- `/api/weather` 需要 `weather.read`，`/api/admin` 需要 `authorization.manage`；
- `AddLedonAuthorization` 默认启用管理端点，并内置 `admin` 角色；
- 生产环境请替换 `DemoUserValidator`、`IRoleStore` 与 JWT 密钥。

> Blazor 使用 .NET 10 自带组件，不引入付费组件。
