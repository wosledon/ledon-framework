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

## Ledon.FeatureManagement —— 功能开关与灰度

```csharp
builder.Services.AddLedonFeatureManagement();

// 配置（Nacos/Apollo 远程配置同样生效）
// "Features": { "new-checkout": true, "beta-report": { "Percentage": 20 }, "vip-only": { "Tenants": [ "t-001" ] } }
```

- 未登记的开关默认关（fail-closed）；
- 端点级开关：`.AddEndpointFilter(new FeatureGateFilter("beta-report"))`，未启用返回 404；
- 百分比分桶稳定：同一主体判定不变。

## Ledon.BlobStorage —— 文件/对象存储

```csharp
builder.Services.AddLedonBlobStorage(o =>
{
    o.RootPath = "App_Data/blobs";
    o.AllowedExtensions.Add(".png");
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

- 路径即主键；防目录穿越；默认拒绝覆盖；大小上限默认 100MB；
- 多租户默认按 `tenants/{tenantId}/` 加前缀，租户间文件互不可见。

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
- 失败自动重试（默认 3 次，指数退避）；`INotificationLog` 可查发送记录与成功率。

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
- 查询 `IAuditStore.QueryAsync(entityType, entityId)`；保留策略 `PurgeOlderThanAsync`。

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
builder.Services.AddLedonAdmin();   // 前缀 /admin/ops
```

| 端点 | 内容 |
| --- | --- |
| `GET /admin/ops/summary` | 待投递/死信、队列累计指标、运行中工作流、定时任务与近期失败 |
| `GET /admin/ops/outbox/dead-letters` | 搁置消息清单 |
| `POST /admin/ops/outbox/dead-letters/{id}/replay` | 重放单条死信 |
| `GET /admin/ops/scheduler` | 任务清单 + 最近执行历史 |

只有死信重放一个写操作；未接入的能力返回空数据而不是报错。生产环境请加认证授权。

完整文档：<https://github.com/wosledon/ledon-framework>
