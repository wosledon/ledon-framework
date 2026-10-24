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

## 刷新令牌

访问令牌过期后换取新令牌（无需重新登录）：

```csharp
// 登录：两个令牌一起签发
var access = jwt.Generate("u1", "alice", ["users.read"]);
var refreshToken = await refresh.IssueAsync("u1");

// 刷新：轮换换取新刷新令牌（旧的随即作废）
var newRefresh = await refresh.RotateAsync(oldRefreshToken);
if (newRefresh is null) { /* 刷新失败：重新登录 */ }
```

- 刷新令牌为 256-bit 随机不透明串，服务端**只存 SHA-256 哈希**（库被拖库也换不出可用令牌）；
- 默认有效期 7 天：`o.RefreshTokenExpiration` 可调；
- **轮换语义**：每次刷新旧令牌即吊销，新令牌与旧令牌不共存；
- **重放保护**：已吊销的令牌被再次使用（重放）时，该用户全部令牌作废——请引导客户端重新登录；
- 默认进程内存储；多实例部署替换为共享存储：实现 `IRefreshTokenStore`（如 Redis）并注册，框架的 TryAdd 注册让位于手动注册。

## 登录/刷新端点

不想自己写登录接口时，框架提供标准端点（实现 `ILedonUserValidator` 即被自动发现）：

```csharp
public class DbUserValidator(MyDbContext db) : ILedonUserValidator
{
    public async Task<LedonUser?> ValidateAsync(string userName, string password, CancellationToken ct = default)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Name == userName, ct);
        return user is not null && BCrypt.Verify(password, user.PasswordHash)
            ? new LedonUser(user.Id, user.Name, user.Permissions)
            : null;
    }

    // 刷新时按 ID 重建身份（不实现则 /auth/refresh 返回 501 提示）
    public Task<LedonUser?> FindByIdAsync(string userId, CancellationToken ct = default) => /* ... */;
}
```

```csharp
app.MapLedonAuthEndpoints();
// POST /auth/login    { userName, password }   → 200 { accessToken, refreshToken, expiresInSeconds } | 401
// POST /auth/refresh  { refreshToken }          → 200 { accessToken, refreshToken, expiresInSeconds } | 401
```

登录成功返回双令牌；`/auth/refresh` 轮换刷新令牌并重新签发访问令牌。需要完整 OIDC/授权服务器（第三方客户端、授权码流）时接 OpenIddict——框架的 `IJwtTokenGenerator`/`IRefreshTokenService` 可作为其令牌签发积木。

## 边界

- 访问令牌本身无吊销列表（JWT 无状态特性）——需要踢人下线时缩短 `o.Jwt.Expiration` + 轮换刷新令牌（吊销刷新令牌即切断续期）；
- API Key 为静态字典配置，适合少量固定调用方；大量动态 Key 请替换为自己的方案（实现 `IAuthenticationHandler` 注册即可）。
