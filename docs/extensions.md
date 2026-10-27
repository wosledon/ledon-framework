# 扩展能力包

跨模块能力包，全部按需引入。第一批覆盖集成/通知/基础设施/安全/测试，第二批补齐配额、租户数据库、账号安全、出站客户端、密钥、数据交换、图像、搜索同步与迁移编排。

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

## 配额、安全与账号

### Ledon.Quotas —— 固定窗口配额/限流

```csharp
builder.Services.AddLedonQuotas(o =>
{
    o.DefaultLimit = 100;
    o.Window = TimeSpan.FromMinutes(1);
    o.UseDistributedCache = true;                 // 多实例共享（需先注册 IDistributedCache）
    o.KeySelector = ctx => ctx.User.FindFirst("tenant")?.Value ?? "anonymous";
});
app.UseLedonQuotas();
```

超限返回 429 + `Retry-After`，响应头带剩余配额；默认按客户端 IP，可按租户/API Key/用户自定义。

### Ledon.Security.ApiKeys / Ledon.Security.TokenStores

动态 API Key（SHA-256 哈希、scopes/租户/过期/吊销、`LedonApiKey` 认证方案）与基于 `IDistributedCache` 的共享刷新令牌存储（单条/按用户吊销）。

### Ledon.Security.Totp —— TOTP 双因素

```csharp
builder.Services.AddLedonTotp(o => o.Issuer = "MyApp");

var secret = totp.GenerateSecret();
var valid = totp.Validate(secret, userCode);
var codes = recoveryCodes.Generate();             // 一次性恢复码，SHA-256 哈希存储
```

RFC 6238（SHA-1/256/512、6-8 位、时间窗口）；`BuildUri` 可直接生成二维码内容。

### Ledon.Security.Accounts —— 注册登录与密码生命周期

```csharp
builder.Services.AddLedonAccounts(o =>
{
    o.RequireConfirmedEmail = true;
    o.RegisterUserValidator = true;               // 接入 /auth/login
});
```

PBKDF2 密码、邮箱确认、失败锁定、一次性密码重置令牌；端点 `/auth/accounts/*`，可接入 `Ledon.Notifications` 发送邮件。

### Ledon.Secrets —— Vault 密钥

```csharp
builder.Configuration.AddLedonVault(o =>
{
    o.Address = "https://vault:8200";
    o.Token = Environment.GetEnvironmentVariable("VAULT_TOKEN");
    o.Paths.Add("myapp");
});

builder.Services.AddLedonSecrets();
var password = await secrets.GetRequiredSecretAsync("myapp/db#password");
```

HashiCorp Vault KV v1/v2、Token/TokenFile、Namespace、TTL 缓存与启动配置源；生产可替换为 KMS 实现。

## 数据与集成

### Ledon.Data.MultiTenancy —— 租户数据库路由

```csharp
builder.Services.AddLedonTenantData<AppDbContext>(o =>
{
    o.DefaultConnectionString = configuration.GetConnectionString("Default");
    o.ConfigureDbContext = (db, cs) => db.UseNpgsql(cs);
});
```

按租户目录解析连接串（共享库回退）、按租户创建 DbContext、默认库 + 全部启用租户迁移编排。

### Ledon.Data.Migrations —— 模块迁移编排

```csharp
builder.Services.AddLedonMigration<AppDbContext>("app", order: 1);

var status = await runner.GetStatusAsync();
var report = await runner.MigrateAllAsync();
```

EF Core Migrate / EnsureCreated、执行顺序、状态查询、失败隔离；与 `Ledon.Data.MultiTenancy` 的按租户迁移互补。

### Ledon.DataExchange —— CSV/Excel

```csharp
[DataColumn("订单号", 1)] public Guid Id { get; set; }
[DataColumn("金额", 2)] public decimal Amount { get; set; }

await exchange.ExportCsvAsync(stream, rows);
var result = await exchange.ImportExcelAsync<OrderRow>(stream);
```

RFC 4180 CSV、OpenXML 流式 Excel、属性映射、逐行错误行号/列名报告。

### Ledon.Imaging —— 图像处理

```csharp
builder.Services.AddLedonImaging();

var bytes = await images.ProcessAsync(stream, new ImageTransform(
    Width: 320, Height: 320, Mode: ResizeMode.Fit, Format: ImageFormat.Webp));
```

SkiaSharp（MIT）PNG/JPEG/WebP、Fit/Fill/Stretch、缩略图与 `IFileStorage` 集成；按平台添加 NativeAssets。

### Ledon.ApiClients —— 出站 HTTP 客户端

```csharp
builder.Services.AddLedonApiClient("orders", o =>
{
    o.BaseAddress = "https://orders.internal/";
    o.TokenEndpoint = "https://identity/connect/token";
    o.ClientId = "...";
    o.ClientSecret = "...";
});
```

OAuth2 client_credentials 令牌缓存/刷新、Bearer、`X-Correlation-ID`、`traceparent` 自动传播；支持命名与类型化客户端。

### Ledon.Search.Sync —— 索引增量同步

```csharp
builder.Services.AddLedonSearchSync(o => o.MaxRetries = 5);

await sync.EnqueueUpsertAsync("products", id, product, tenantId);
await sync.EnqueueDeleteAsync("products", id, tenantId);
```

可替换同步队列 + 后台 Worker + 指数退避重试；生产可把 `ISearchSyncQueue` 换成数据库发件箱/持久队列。

## API 治理

### Ledon.Idempotency —— 幂等与防重复提交

```csharp
builder.Services.AddLedonIdempotency(o => o.Ttl = TimeSpan.FromHours(24));
app.UseRouting();
app.UseLedonIdempotency();
```

`Idempotency-Key` + 请求指纹 + 响应重放 + 同键冲突 409 + 并发单飞去重；注册分布式锁/缓存后支持多实例。默认按租户 + 用户分区，避免撞键。

### Ledon.Security.Authorization —— RBAC

```csharp
builder.Services.AddLedonAuthorization(o =>
{
    o.BootstrapAdminUserIds.Add("admin");   // 首个管理员用户 ID
    o.MapEndpoints = true;                  // 默认已开启
});
```

- 内置 `admin` 角色（权限 `*`），`BootstrapAdminUserIds` 中的用户启动即拥有管理员权限；
- 管理端点 `/auth/authorization/*` 默认映射，并要求 `authorization.manage` 权限；
- `GET /auth/authorization/me` 返回当前用户角色/权限/是否管理员；
- 角色权限支持通配符，分配可按租户隔离；
- 直接可用不代表零配置安全：必须配置认证（如 `AddLedonSecurity`）并设置管理员引导用户；生产环境替换 `IRoleStore`。

### Ledon.ApiVersioning —— API 版本治理

```csharp
builder.Services.AddLedonApiVersioning(o => o.DefaultVersion = "1.0");
app.UseRouting();
app.UseLedonApiVersioning();
```

URL 段 / `X-Api-Version` / `api-version` / 媒体类型多来源版本；`[ApiDeprecated]` 输出 `Deprecation`/`Sunset`/`Link`。基于 Apache-2.0 的 `Asp.Versioning`。

> 许可证说明：本仓库扩展模块只选用 MIT / Apache-2.0 等宽松开源组件（如 SkiaSharp、OpenXML、Asp.Versioning），不引入付费或需商业授权的组件。
