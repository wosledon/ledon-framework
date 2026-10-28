# Ledon.Security.ApiKeys

动态 API Key：创建、SHA-256 哈希存储、scopes/租户绑定、过期/吊销、认证方案。

```csharp
builder.Services.AddLedonApiKeys(o =>
{
    o.HeaderName = "X-Api-Key";
    o.RequireAuthorization = true;
});
```

- 认证方案：`LedonApiKey`（`AuthorizationPolicyBuilder` 可同时接受 JWT + API Key）；
- Key 明文只在创建时返回一次，库中只存 SHA-256 哈希；
- 支持 scopes（写入 `permission` claim）、租户、过期时间、吊销、最后使用时间；
- 管理端点：`GET/POST /auth/api-keys`、`DELETE /auth/api-keys/{id}`。

> 与 `Ledon.Security` 的静态 `ApiKeyOptions.Keys` 是两套机制：静态适合少量固定调用方，本包适合需要轮换/吊销/租户的动态场景。

- 认证失败统一返回 401/403，不区分"Key 不存在"与"已过期"，避免枚举；
- 创建/吊销端点默认要求已认证，生产环境应叠加管理员策略（如 `Ledon.Security.Authorization` 的权限）；
- 每次校验都会更新最后使用时间（可关闭），便于识别长期未使用的 Key。

完整文档：<https://github.com/wosledon/ledon-framework>
