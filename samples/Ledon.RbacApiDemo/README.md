# Ledon.RbacApiDemo

一个可直接运行的 API 示例，展示：

- JWT 登录与内置 RBAC（admin 引导、reader 角色）；
- `/auth/authorization/me` 与角色管理端点；
- `Idempotency-Key` 防重复下单；
- 按用户/IP 的固定窗口配额；
- API 版本化与 `[ApiDeprecated]` 响应头；
- OpenAPI + Scalar UI。

## 运行

```bash
dotnet run --project samples/Ledon.RbacApiDemo
```

打开 <http://localhost:5100/scalar> 查看接口文档。

## 演示账号

| 账号 | 密码 | 角色 |
|---|---|---|
| `admin` | `admin123` | `admin`（权限 `*`） |
| `user` | `user123` | `reader`（`weather.read`、`orders.write`） |

## 快速验证

```powershell
$login = Invoke-RestMethod http://localhost:5100/auth/login -Method Post `
  -ContentType application/json -Body '{"userName":"user","password":"user123"}'

$headers = @{ Authorization = "Bearer $($login.accessToken)"; "Idempotency-Key" = "order-001" }
Invoke-RestMethod http://localhost:5100/api/orders -Method Post -Headers $headers `
  -ContentType application/json -Body '{"product":"book","amount":39.9}'

# 相同 Idempotency-Key 重放，不会重复创建订单
Invoke-RestMethod http://localhost:5100/api/orders -Method Post -Headers $headers `
  -ContentType application/json -Body '{"product":"book","amount":39.9}'

# 普通用户访问管理员接口 → 403
Invoke-RestMethod http://localhost:5100/api/admin -Headers @{ Authorization = "Bearer $($login.accessToken)" }
```

> 示例全部使用内存存储；生产替换为真实用户、角色存储与分布式幂等/配额实现。
