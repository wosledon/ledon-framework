# Ledon.ConfigCenterDemo

一个基于现有框架模块实现的**完整配置中心**示例：

- 项目管理：创建/查看/更新/删除项目，项目 key 唯一；
- 环境管理：每个项目默认 `dev` / `test` / `prod`，支持新增/删除环境；
- 配置管理：按项目 + 环境 + key 管理 JSON 值，自动版本号与历史；
- 版本历史：查看历史、回滚到任意版本；
- 权限管理：
  - 系统管理员（内置 RBAC `admin`，权限 `*`）可管理所有项目；
  - 项目级角色：`Owner` / `Maintainer` / `Viewer`；
  - `config.read` / `config.write` / `config.publish` / `members.manage` / `project.manage`；
  - 非管理员只能看到自己参与的项目；
- 数据持久化：EF Core + SQLite（`App_Data/config-center.db`）；
- 管理页面 + OpenAPI / Scalar。

## 运行

```bash
dotnet run --project samples/Ledon.ConfigCenterDemo
# 或
./run.ps1 config
```

- 管理页面：<http://localhost:5120>
- Scalar API：<http://localhost:5120/scalar>

## 演示账号

| 账号 | 密码 | 权限 |
|---|---|---|
| `admin` | `admin123` | 系统管理员，所有项目 |
| `user` | `user123` | `demo` 项目 Maintainer（可读写配置） |
| `viewer` | `viewer123` | `demo` 项目 Viewer（只读） |

## 主要 API

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/projects` | 当前用户可见项目 |
| POST | `/api/projects` | 创建项目（自动建 dev/test/prod 环境） |
| GET | `/api/projects/{id}` | 项目详情/当前权限 |
| PUT/DELETE | `/api/projects/{id}` | 项目重命名/删除 |
| GET/POST | `/api/projects/{id}/environments` | 环境列表/新增 |
| GET/POST | `/api/projects/{id}/members` | 成员列表/分配角色 |
| GET | `/api/projects/{id}/configs?environment=dev` | 配置列表 |
| GET/PUT/DELETE | `/api/projects/{id}/configs/{key}?environment=dev` | 读取/写入/删除 |
| GET | `/api/projects/{id}/configs/{key}/history` | 版本历史 |
| POST | `/api/projects/{id}/configs/{key}/rollback/{version}` | 回滚 |
| GET | `/api/projects/{id}/configs/export` | 导出环境配置 JSON |

## 权限模型

```text
系统 admin（内置 RBAC） → 全部项目、全部操作
项目 Owner             → 配置读写/发布 + 成员 + 项目设置
项目 Maintainer        → 配置读写/发布
项目 Viewer            → 配置只读
```

项目成员、环境、配置、版本记录都持久化在 SQLite；生产环境可替换为 PostgreSQL/MySQL，并接入 `Ledon.Settings`/配置中心客户端做热更新。

> 示例 JWT 签名密钥与演示账号仅用于本地；生产请替换 `ILedonUserValidator`、配置真实密钥，并用 `AddLedonAuthorization` 的系统管理员引导机制保护管理入口。

