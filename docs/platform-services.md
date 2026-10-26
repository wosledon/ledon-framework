# 新能力包：锁 / 开关 / 存储 / 通知 / 本地化 / 审计 / 应用层 / 运维

一轮补齐的 8 个能力包。共同原则：**业务只依赖抽象，换实现不改代码**；未接入的能力在运行时为 `null`，不报错。

## Ledon.Locking —— 分布式锁

```csharp
builder.Services.AddLedonLocking();                                  // 进程内（单实例）
builder.Services.AddLedonLocking(o => o.Provider = LedonLockProvider.Redis,
    o => o.RedisConnectionString = "localhost:6379");                 // 多实例
```

- `TryAcquireAsync` 不等待（拿不到返回 `null`）；`AcquireAsync` 等待超时抛 `TimeoutException`；
- 持锁默认 30 秒到期自动释放（持锁进程崩溃不死锁）；
- Redis 实现 SET NX PX + Lua 校验令牌释放，不会误删他人已到期的锁。

### 长临界区必须续期（看门狗）

锁是**固定 TTL** 的：临界区执行超过 TTL 时锁已过期，别的进程能拿到同一把锁，
两个进程同时认为持有（脑裂）。`ILockHandle.RenewAsync` + `LockWatchdog` 把这件事变成两行：

```csharp
await using var lease = await locker.AcquireAsync("job:nightly", expiry: TimeSpan.FromSeconds(30));
await using var watchdog = lease.WithWatchdog(logger, expiry: TimeSpan.FromSeconds(30));

watchdog.LockLost += () => logger.LogWarning("锁丢了，必须停止工作");

// 长任务用 watchdog.Lifetime：续期失败/锁丢失时它会被取消
await NightlyJobAsync(watchdog.Lifetime);
```

- 看门狗按 `expiry / 3` 续期（下限 1 秒），续期是 Lua 原子操作——不会把别人的锁续命；
- `RenewAsync` 返回 `false` / 续期调用本身失败，都按"锁已丢"处理并取消 `Lifetime`；
- 进程内实现没有"过期"概念，`RenewAsync` 恒成功（语义一致，业务不必区分部署形态）。

## Ledon.FeatureManagement —— 功能开关与灰度

```csharp
builder.Services.AddLedonFeatureManagement();

// 配置（Nacos/Apollo 远程配置同样生效）
// "Features": { "new-checkout": true, "beta-report": { "Percentage": 20 }, "vip-only": { "Tenants": [ "t-001" ] } }
```

- 未登记的开关默认关（fail-closed）；
- 端点级开关：`.AddEndpointFilter(new FeatureGateFilter("beta-report"))`，未启用返回 404；
- 百分比分桶稳定：同一主体判定不变——用 FNV-1a 对 `开关名:主体键` 取模，
  **跨进程/跨重启一致**（`HashCode.Combine` 每进程随机种子，会让同一用户在不同实例落到不同桶）。

## Ledon.MultiTenancy —— 多租户

```csharp
builder.Services.AddLedonMultiTenancy(o =>
{
    o.RequireTenant = true;    // 强租户模式：漏带 X-Tenant-Id 直接 400，不按单租户放行
    o.TenantValidator = new MyTenantValidator();   // 可选：校验租户 ID 真实存在/启用
});
app.UseLedonMultiTenancy();
```

- 解析来源：请求头 / 查询串 / 用户声明 / Host 子域名（`TenantSource`）；
- **`RequireTenant`**：多租户 SaaS 应该打开。否则漏带头的请求会静默看到全量数据
  （跨租户泄漏），而且日志里只有一行 Debug，极难发现；
- **`TenantValidator`**：不信任请求里的租户 ID，否则客户端可以枚举；
- 环境租户随请求管线流动（`AsyncLocal`），后台任务也拿得到；
- 缓存键自动按租户加前缀（配了缓存包时），不配则租户间互串。

## Ledon.SignalR —— 实时推送

```csharp
builder.Services.AddLedonSignalR(o => o.RedisConnectionString = "localhost:6379");
app.MapLedonSignalRHubs();   // ChatHub → /hubs/chat

// 在线状态自动维护：Hub 继承 OnlineAwareHub 即可，不用手写 OnConnectedAsync
public class ChatHub : OnlineAwareHub
{
    public Task Send(string text) => Clients.All.SendAsync("recv", text);
}
```

- Hub 自动发现 + 约定路径映射；多实例部署挂 Redis backplane（跨实例广播）；
- 在线注册表：配了 Redis 就用 Redis 实现（全局在线视图 + 跨实例踢下线）；
  未配则进程内（只覆盖本实例连接）；
- 连接登记带 TTL 兜底：进程被杀时 `OnDisconnectedAsync` 不会执行，靠过期自动清理。

## Ledon.BlobStorage —— 文件/对象存储

```csharp
// 本地磁盘
builder.Services.AddLedonBlobStorage(o =>
{
    o.Provider = LedonBlobProvider.Local;
    o.RootPath = "App_Data/blobs";
    o.AllowedExtensions.Add(".png");
});

// S3 / MinIO（路径风格，自动建桶；AWS 官方 S3 可省略 ServiceUrl）
builder.Services.AddLedonBlobStorage(o =>
{
    o.Provider = LedonBlobProvider.S3;
    o.ServiceUrl = "http://localhost:9000";
    o.BucketName = "uploads";
    o.AccessKey = "<从密钥管理注入>";
    o.SecretKey = "<从密钥管理注入>";
    o.Region = "us-east-1";
});

public class AvatarService(IFileStorage storage)
{
    public async Task<string> UploadAsync(Stream content, CancellationToken ct)
    {
        var path = $"avatars/{Guid.NewGuid():N}.png";
        await storage.SaveAsync(path, content, ct: ct);
        return path;
    }
}
```

- 路径即主键；防目录穿越；默认拒绝覆盖（S3 用 `If-None-Match: *`）；大小上限默认 100MB；
- 本地写入先落临时文件再原子替换：超限/失败不会留下半截文件；
- 多租户默认按 `tenants/{tenantId}/` 加前缀，租户间文件互不可见；`ListAsync` 同样限定在当前租户；
- S3 兼容 MinIO（`ForcePathStyle = true`）；未配置访问密钥时走 AWS 默认凭证链。

## Ledon.Notifications —— 通知发送

```csharp
builder.Services.AddLedonNotifications(o =>
{
    o.SmtpHost = "smtp.example.com";
    o.FromAddress = "noreply@example.com";
});

await sender.SendAsync(new Notification("email", "a@b.c", "欢迎注册", "感谢注册"), ct);
```

- 渠道可替换（实现 `INotificationSender` 即接入短信/推送）；
- 失败自动重试（`MaxRetries` 默认 3 次，指数退避：`RetryBaseDelay × 2^(n-1)`，
  封顶 `RetryMaxDelay`，可选 ±25% 抖动——批量同时失败时不挤在同一时刻重试）；
- 批量发送有界并发（`BatchConcurrency` 默认 8；串行发送 100 条要约 100 倍延迟）；
- 消息去重（`DeduplicationKey`）：渠道实际送达但返回失败时不会让用户收到重复通知；
- `INotificationLog` 可查发送记录与成功率（`Attempts` 记真实尝试次数）。

## Ledon.Localization —— 多语言

```csharp
builder.Services.AddLedonLocalization(o => { o.DefaultCulture = "zh-CN"; o.SupportedCultures.Add("en-US"); });
app.UseLedonRequestLocalization();   // 放在 UseRouting 之前
```

- 文化解析优先级：查询串 → Cookie → `Accept-Language`（按 q 值）→ 默认；
- 语言相同地区不同时回退（`zh-TW` → `zh-CN`）；名单外文化被忽略；
- 文案来自 `Resources/{culture}.json`，改文件即时生效。

## Ledon.AuditLog —— 操作审计

```csharp
builder.Services.AddLedonAudit<AppDbContext>();
app.UseLedonAuditContext();
```

- 零改动：EF SaveChanges 拦截器自动记录新增/修改/删除，修改只记**真实变化**的属性；
- 审计写在业务事务提交**之后**：业务回滚不留假记录；审计自身失败只记日志；
- 操作人来自登录声明；后台任务用 `AuditContext.BeginScope(userId, name)` 声明；
- 查询 `IAuditStore.QueryAsync(entityType, entityId)`；保留策略 `PurgeOlderThanAsync` 也由后台自动执行：
  `RetentionDays`（默认 90，0 = 关闭）按 `RetentionSweepInterval`（默认 1 小时）清理过期流水。

## Ledon.Application —— 应用层管线（CQRS）

```csharp
builder.Services.AddLedonApplication(o =>
{
    o.UseValidation();     // IValidatable 请求先校验
    o.UseLogging();        // 请求类型 + 耗时 + span
    o.UseTransactions();   // 包进 IUnitOfWork（未注册则直通）
    o.UseCaching();        // ICacheableQuery 自动走缓存
});

public sealed record CreateOrder(string Sku, int Qty) : ICommand<Guid>, IValidatable
{
    public IReadOnlyCollection<string> Validate() => Qty <= 0 ? ["数量必须大于 0"] : [];
}

public class CreateOrderHandler(IUnitOfWork uow) : ICommandHandler<CreateOrder, Guid> { /* 只管业务 */ }

var id = await sender.SendAsync(new CreateOrder("SKU-1", 2), ct);
```

处理器经约定扫描自动注册；行为按登记顺序织入（外层先执行）；自定义行为实现 `IPipelineBehavior<TRequest>`。

## Ledon.Admin —— 统一运维端点

```csharp
builder.Services.AddLedonAdmin(o =>
{
    o.RoutePrefix = "/ops";            // 默认 /admin/ops（首尾斜杠自动规范化）
    o.RequireAuthorization = true;     // 生产建议开启；需同时注册认证方案，否则启动失败
});
```

| 端点（`{前缀}` = `RoutePrefix`） | 内容 |
| --- | --- |
| `GET {前缀}/summary` | 待投递/死信、队列累计指标、运行中工作流、定时任务与近期失败 |
| `GET {前缀}/outbox/dead-letters` | 搁置消息清单 |
| `POST {前缀}/outbox/dead-letters/{id}/replay` | 重放单条死信 |
| `GET {前缀}/scheduler` | 任务清单 + 最近执行历史 |

只有死信重放一个写操作；未接入的能力返回空数据而不是报错。运维端点会暴露内部运行信息：
生产环境请开启 `RequireAuthorization` 或在外层网关做访问控制；只声明授权而未注册认证方案时，
框架会在启动阶段直接报错，避免静默裸奔。

完整文档：<https://github.com/wosledon/ledon-framework>
