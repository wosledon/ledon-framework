# Ledon.Secrets

HashiCorp Vault KV（v1/v2）密钥读取与 `IConfiguration` 配置源；带 TTL 缓存与内存开发实现。

```csharp
// 启动配置（Vault 不可用时可 Optional 容错）
builder.Configuration.AddLedonVault(o =>
{
    o.Address = "https://vault:8200";
    o.Token = Environment.GetEnvironmentVariable("VAULT_TOKEN");
    o.Paths.Add("myapp");
    o.KeyPrefix = "Vault:";
});

// 运行时按需读取
builder.Services.AddLedonSecrets(o =>
{
    o.Address = "https://vault:8200";
    o.Token = Environment.GetEnvironmentVariable("VAULT_TOKEN");
    o.CacheDuration = TimeSpan.FromMinutes(5);
});

var password = await secrets.GetRequiredSecretAsync("myapp/db#password");
```

- 支持 KV v2（默认）与 KV v1，Token 可来自 `Token` / `TokenFile` / `VAULT_TOKEN`；
- 支持 Vault Enterprise `Namespace`；
- 配置源加载 `Paths` 下字段；`IncludePathInKey`/`KeyPrefix` 控制键名；
- `VaultOptions.Optional = true` 时 Vault 不可用不阻塞启动；
- 密钥不写日志；生产建议使用 Vault AppRole/Kubernetes 认证后注入 Token；
- `ISecretProvider` 可替换为 KMS/云密钥管理实现。

完整文档：<https://github.com/wosledon/ledon-framework>
