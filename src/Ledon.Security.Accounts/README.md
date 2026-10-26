# Ledon.Security.Accounts

账号生命周期：注册、邮箱确认、PBKDF2 密码、失败锁定、忘记/重置密码，可选接入 `Ledon.Notifications` 发送邮件。

```csharp
builder.Services.AddLedonAccounts(o =>
{
    o.RequireConfirmedEmail = true;
    o.MaxFailedAccessAttempts = 5;
    o.LockoutDuration = TimeSpan.FromMinutes(5);
    o.RegisterUserValidator = true; // 让 /auth/login 使用账号登录
});
```

端点（`MapEndpoints` 默认开启，由 `UseLedonWeb()` 自动映射）：

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `/auth/accounts/register` | 注册；返回 201 |
| POST | `/auth/accounts/verify-email` | 确认邮箱 |
| POST | `/auth/accounts/resend-verification` | 重发确认 |
| POST | `/auth/accounts/forgot-password` | 请求重置（始终 202，不暴露账号是否存在） |
| POST | `/auth/accounts/reset-password` | 重置密码 |

服务用法：

```csharp
var result = await accounts.RegisterAsync("alice", "alice@example.com", "S3cret-pass");
await accounts.ConfirmEmailAsync(result.Account.Id, result.EmailVerificationToken!);
var login = await accounts.AuthenticateAsync("alice", "S3cret-pass");
if (login.Status == AccountLoginStatus.Success) { /* login.User */ }
```

- 密码使用 PBKDF2-SHA256（10 万次迭代、随机盐）哈希，常量时间校验；
- 确认/重置令牌只存 SHA-256 哈希、一次性、带过期时间，重发同用途令牌会使旧令牌失效；
- 默认内存存储；生产多实例请替换 `IAccountStore` 与 `IAccountTokenStore`（数据库/Redis）；
- `ExposeTokensInResponses` 仅用于本地开发；生产请配置通知渠道；
- `AccountsUserValidator` 默认不自动注册（`[DisableAutoDiscovery]`），需要时打开 `RegisterUserValidator`。

完整文档：<https://github.com/wosledon/ledon-framework>
