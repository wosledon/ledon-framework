# Ledon React App

Ledon Framework + React（Vite）全栈模板，内置可直接使用的 RBAC。

## 启动

```bash
# 终端 1：后端（http://localhost:5000）
dotnet run

# 终端 2：前端（http://localhost:5173，/api 与 /auth 自动代理到后端）
cd ClientApp
npm install
npm run dev
```

生产构建：

```bash
cd ClientApp
npm run build        # 输出到 ../wwwroot
cd ..
dotnet run
```

## 演示账号

| 账号 | 密码 | 角色/权限 |
|---|---|---|
| `admin` | `admin123` | 内置 `admin` 角色，权限 `*` |
| `user` | `user123` | `reader`，权限 `weather.read` |

## 内置 RBAC

- `AddLedonAuthorization` 默认映射 `/auth/authorization/*`，管理接口要求 `authorization.manage`；
- `BootstrapAdminUserIds` 把首个管理员用户 ID 自动分配到内置 `admin` 角色；
- `GET /auth/authorization/me` 返回当前用户角色、权限与是否管理员；
- 示例策略：`weather.read`、`authorization.manage`；
- 生产环境请替换 `DemoUserValidator` 与默认 `IRoleStore`，并修改 JWT 签名密钥。

> 该模板使用 React 19 + Vite；不包含任何付费前端组件。
