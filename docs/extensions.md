# 扩展能力包

本轮补齐的 11 个跨模块能力，全部按需引入。

## 集成与通知

### Ledon.Webhooks —— 出站 Webhook

```csharp
builder.Services.AddLedonWebhooks(o => o.RequireAuthorization = true);
```

订阅、HMAC-SHA256 签名、指数退避重试、死信记录/重放；管理端点 `/webhooks/*`。
签名算法：`HMAC-SHA256(secret, "{X-Ledon-Timestamp}.{body}")`。

### Ledon.Notifications.Sms / Push

```csharp
builder.Services.AddLedonNotifications();
builder.Services.AddLedonSmsNotifications(o => { o.Provider = SmsProvider.Twilio; /* ... */ });
builder.Services.AddLedonPushNotifications(o => { o.Provider = PushProvider.Fcm; /* ... */ });
```

- 短信：Webhook / Twilio；
- 推送：Webhook / FCM HTTP v1；
- 都接入 `INotificationSender` 的统一路由、重试与发送记录。

## 基础设施

### Ledon.Ids —— 分布式 ID

```csharp
builder.Services.AddLedonIds(o => o.WorkerId = 1);   // 多实例唯一
```

ULID（同毫秒单调、可按时间排序）、GUID v7、Snowflake。

### Ledon.Data.Seeding —— 模块化播种

```csharp
builder.Services.AddLedonData();
builder.Services.AddLedonDataSeeding();
```

实现 `IDataSeeder` 即自动发现；按 `Order` 顺序执行，支持 `FailFast`。
播种逻辑必须幂等。

### Ledon.Settings —— 运行时设置

```csharp
builder.Services.AddLedonSettings(o => o.StorePath = "App_Data/settings.json");
```

强类型读写 + JSON 持久化 + Data Protection 加密（`encrypted: true`）。
多实例请替换 `ISettingsStore` 为共享数据库实现。

### Ledon.Data.Encryption —— 列级加密

```csharp
builder.Services.AddLedonDataEncryption();

protected override void OnModelCreating(ModelBuilder builder) =>
    builder.ApplyEncryption(encryption);
```

`[Encrypted]` 字符串属性透明加解密；历史明文无前缀时兼容读取。

## 安全与租户

### Ledon.Security.ExternalLogins —— 外部登录

```csharp
builder.Services.AddLedonSecurity(o => o.Jwt.SigningKey = "...");
builder.Services.AddLedonExternalLogins(o => o.Providers.Add(new ExternalLoginProviderOptions
{
    Name = "github",
    Type = ExternalLoginProviderType.GitHub,
    ClientId = "...",
    ClientSecret = "...",
}));
```

- `GET /auth/external/{provider}/challenge`
- `GET /auth/external/{provider}/callback` → 框架双令牌

OIDC / GitHub；state 用 Data Protection 加密并过期。

### Ledon.MultiTenancy.Management —— 租户目录

```csharp
builder.Services.AddLedonTenantManagement(o => o.RequireAuthorization = true);
```

`/tenants` CRUD + `ITenantProvisioner` 开通/回收钩子（创建失败回滚）。
默认内存 `ITenantStore`，可替换为数据库。

## 搜索与测试

### Ledon.Search

```csharp
builder.Services.AddLedonSearch(o =>
{
    o.Provider = SearchProvider.Meilisearch;
    o.Endpoint = "http://localhost:7700";
});
```

`ISearchService`：索引/删除/搜索；内存实现用于开发测试，Meilisearch 用于生产；支持租户过滤。

### Ledon.Testing.Containers

```csharp
if (!await LedonContainers.DockerAvailableAsync()) return;

await using var redis = await RedisFixture.StartAsync();
```

Redis / PostgreSQL / RabbitMQ / Kafka / MinIO 夹具；无 Docker 环境显式跳过。
