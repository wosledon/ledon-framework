# Ledon.Security.ExternalLogins

外部登录：OIDC 授权码流程与 GitHub OAuth App，接入框架 JWT + 刷新令牌。

```csharp
builder.Services.AddLedonSecurity(o => o.Jwt.SigningKey = "...");
builder.Services.AddLedonExternalLogins(o =>
{
    o.Providers.Add(new ExternalLoginProviderOptions
    {
        Name = "github",
        Type = ExternalLoginProviderType.GitHub,
        ClientId = "...",
        ClientSecret = "...",
    });
});
```

端点：

- `GET /auth/external/{provider}/challenge?returnUrl=/home` → 302 到提供程序授权页；
- `GET /auth/external/{provider}/callback?code=&state=` → 200 `{ accessToken, refreshToken, expiresInSeconds }`。

- `state` 用 Data Protection 加密并带过期时间，防篡改/重放；
- 默认 `IExternalUserResolver` 把用户映射为 `{provider}:{subject}`；实现该接口可做本地用户绑定/自动注册/租户选择；
- OIDC 需要配置 `AuthorizationEndpoint` / `TokenEndpoint` / `UserInfoEndpoint`；GitHub 使用默认端点。

完整文档：<https://github.com/wosledon/ledon-framework>
