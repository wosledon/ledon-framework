# Ledon.OpenIddict

Ledon Framework 的 OIDC 服务器：标准 `/connect/token`（password / refresh_token / client_credentials），JWT 访问令牌，与框架的 `ILedonUserValidator` 组合。

```csharp
builder.AddLedonOpenIddict(o =>
{
    o.Clients.Add(new OpenIddictClientDefinition
    {
        ClientId = "webapp",
        ClientSecret = "webapp-secret",              // 空 = 公共客户端
        GrantTypes = ["password", "refresh_token"],
        Scopes = ["openid", "profile", "offline_access"],
    });
});
```

```http
POST /connect/token
grant_type=password&client_id=webapp&client_secret=***&username=alice&password=***&scope=openid profile offline_access
→ { "access_token": "...", "token_type": "Bearer", "expires_in": 3600, "refresh_token": "..." }
```

- **凭证校验**：password 授权走框架的 `ILedonUserValidator`（实现即自动发现）；client_credentials 按客户端定义的权限直接签发（OpenIddict 要求机密客户端带 client_secret）；
- **客户端管理**：配置驱动，宿主启动时经 OpenIddict 管理器创建（客户端密钥走其内置哈希约定，不明文落库）；
- **访问令牌**：标准 JWT（JWS，`sub`/`name`/权限 claim 均带 destination），有效期 `o.AccessTokenLifetime`；
- **刷新令牌**：轮换语义 + `offline_access` scope 门控，有效期 `o.RefreshTokenLifetime`；
- **发现文档/JWKS**：`/.well-known/openid-configuration` 与 `/.well-known/jwks.json` 由 OpenIddict 自动提供；
- **密钥**：默认进程内临时 RSA 签名密钥（单实例开箱即用）；**多实例部署必须配共享密钥**（证书或导出的 RSA 参数，见 OpenIddict `AddSigningKey` 重载），否则其它实例无法验令牌；
- **存储**：默认内存（客户端 + 刷新流程，单实例有效）；多实例部署换 OpenIddict 官方的 EF/Redis 存储（删掉本包注册的四个默认存储，注册自己的即可）。

与轻量方案的关系：只需"登录换令牌"时用 `Ledon.Security` 的 `/auth/login`、`/auth/refresh` 即可；需要标准 OIDC 端点/发现文档/第三方客户端时才上本包。

完整文档：<https://github.com/wosledon/ledon-framework>
