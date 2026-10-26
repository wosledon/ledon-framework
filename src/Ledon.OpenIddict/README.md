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

## 单实例（默认内存存储）

- **凭证校验**：password 授权走框架的 `ILedonUserValidator`（实现即自动发现）；client_credentials 按客户端定义的权限直接签发；
- **客户端管理**：配置驱动，宿主启动时经 OpenIddict 管理器创建（密钥走其内置哈希约定，不明文落库）；
- **访问令牌**：标准 JWT（JWS），有效期 `o.AccessTokenLifetime`；
- **刷新令牌**：轮换语义 + `offline_access` scope 门控，有效期 `o.RefreshTokenLifetime`；
- **发现文档/JWKS**：`/.well-known/openid-configuration` 与 `/.well-known/jwks.json` 由 OpenIddict 自动提供；
- **密钥**：默认进程内临时 RSA（重启后旧令牌失效）。

## 多实例（EF Core 共享存储）

```csharp
builder.Services.AddDbContext<LedonOpenIddictDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("OpenIddict")));

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo("/var/ledon/keys"))
    .SetApplicationName("my-app");       // 所有实例一致

builder.AddLedonOpenIddict(o =>
{
    o.Storage = OpenIddictStorage.EntityFrameworkCore;
    o.EnsureDatabaseCreated = true;      // 开发/测试；生产建议 EF 迁移
    o.SigningCertificatePath = "/var/ledon/signing.pfx";   // 所有实例同一张
    o.SigningCertificatePassword = "...";
    o.Clients.Add(new OpenIddictClientDefinition { ClientId = "webapp", ClientSecret = "..." });
});
```

共享数据库（客户端/授权/scope/令牌）、签名证书（访问令牌验签）与 Data Protection 密钥环（刷新令牌解密）
后，一个实例签发的令牌另一个实例可直接使用。客户端与 scope 在启动时幂等创建。

与轻量方案的关系：只需"登录换令牌"时用 `Ledon.Security` 的 `/auth/login`、`/auth/refresh` 即可；需要标准 OIDC 端点/发现文档/第三方客户端时才上本包。

完整文档：<https://github.com/wosledon/ledon-framework>