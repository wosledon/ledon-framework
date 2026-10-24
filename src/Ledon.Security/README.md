# Ledon.Security

Ledon Framework 的认证授权：JWT Bearer + API Key 双方案、权限策略、刷新令牌。

```csharp
builder.Services.AddLedonSecurity(o => o.Jwt.SigningKey = Configuration["Jwt:Key"]!);
```

```csharp
[Authorize]                          // JWT 或 API Key 任一通过
[Authorize(Policy = "users.read")]   // 权限策略（策略名即权限名）
```

`UseLedonWeb()` 检测到认证方案后自动接线 `UseAuthentication`/`UseAuthorization`。

```csharp
var access = jwt.Generate("u1", "alice", ["users.read"]);   // IJwtTokenGenerator
var refresh = await refreshService.IssueAsync("u1");        // IRefreshTokenService
var next = await refreshService.RotateAsync(oldRefresh);    // 轮换（旧的即作废）
```

刷新令牌为随机不透明串，服务端只存 SHA-256 哈希；检测到重放时吊销该用户全部令牌。默认进程内存储，多实例部署可替换 `IRefreshTokenStore`。

完整文档：<https://github.com/wosledon/ledon-framework>
