# 认证授权

```csharp
builder.Services.AddLedonSecurity(o => o.Jwt.SigningKey = Configuration["Jwt:Key"]!);
```

`UseLedonWeb()` 检测到认证方案后自动接线 `UseAuthentication`/`UseAuthorization`——无需手动调用。

## 双方案，任一通过

```csharp
[Authorize]                          // JWT Bearer 或 API Key 任一通过
[HttpGet] public object WhoAmI() => new { User.Identity?.Name };
```

| 方式       | 配置                                          | 适用                 |
| ---------- | --------------------------------------------- | -------------------- |
| JWT        | `o.Jwt.SigningKey`（至少 32 字符）            | 终端用户（前端/App） |
| API Key    | `o.ApiKey.Keys = { ["key-1"] = "service-a" }` | 服务间调用、内部系统 |

API Key 默认走 `X-Api-Key` 请求头（`o.ApiKey.HeaderName` 可改）。JWT 的签发者/受众/有效期走 `o.Jwt.Issuer` / `Audience` / `Expiration`。

## 签发令牌

```csharp
var generator = provider.GetRequiredService<IJwtTokenGenerator>();
var token = generator.Generate("u1", "alice", ["users.read", "users.write"]);
```

## 权限策略（策略名 = 权限名）

```csharp
[Authorize(Policy = "users.read")]   // 令牌里带 users.read 权限才放行
[HttpGet] public List<User> List() => users;
```

权限以 claim 形式写入令牌（claim 类型默认 `permission`，`o.PermissionClaimType` 可改），策略提供器把任意策略名解析为权限检查——**不需要预先注册策略**。

## 当前用户

```csharp
public class AuditFilter(ICurrentUser user)
{
    public void Record() => logger.LogInformation("{User} 执行了操作", user.UserName);
}
```

`ICurrentUser` 暴露 `IsAuthenticated` / `UserId` / `UserName` / `HasPermission(p)` / `Permissions`。非 Web 场景（后台任务）下为匿名。

## 边界

- 只发**访问令牌**，无 refresh token 流程（过期重新登录/签发）；无令牌吊销列表（需要踢人下线时缩短有效期 + 换签名密钥）；
- API Key 为静态字典配置，适合少量固定调用方；大量动态 Key 请替换为自己的方案（实现 `IAuthenticationHandler` 注册即可）。
