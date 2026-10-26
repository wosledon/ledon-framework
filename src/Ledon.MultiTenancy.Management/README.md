# Ledon.MultiTenancy.Management

租户目录管理：租户 CRUD + 开通/回收钩子，配合 `Ledon.MultiTenancy` 的解析与过滤使用。

```csharp
builder.Services.AddLedonTenantManagement(o => o.RequireAuthorization = true);

builder.Services.AddSingleton<ITenantProvisioner, MyTenantProvisioner>();
```

端点：

- `GET /tenants`
- `GET /tenants/{id}`
- `POST /tenants` `{ id, name?, connectionString? }`
- `PUT /tenants/{id}` `{ name?, connectionString?, active }`
- `DELETE /tenants/{id}`

- 默认内存 `ITenantStore`，可替换为数据库实现；
- `ITenantProvisioner` 在创建/更新时调用 `ProvisionAsync`，删除时调用 `DeprovisionAsync`；创建失败会回滚目录项；
- `ConnectionString` 属敏感信息，建议结合 `Ledon.Settings`/列加密存储；
- 生产环境请开启 `RequireAuthorization`。

完整文档：<https://github.com/wosledon/ledon-framework>
