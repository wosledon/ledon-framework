# Ledon.Security.TokenStores

基于 `IDistributedCache` 的共享刷新令牌存储，替代 `Ledon.Security` 默认的进程内实现。

```csharp
builder.Services.AddLedonCaching();          // 或 AddLedonDistributedCaching(Redis)
builder.Services.AddLedonSecurity(o => o.Jwt.SigningKey = "...");
builder.Services.AddLedonDistributedTokenStores(o => o.KeyPrefix = "myapp:tokens");
```

- 适用于多实例部署：所有实例共享 Redis/分布式缓存；
- 仍只存 SHA-256 哈希，不存明文刷新令牌；
- 支持单条吊销与按用户全量吊销；
- 用户索引采用读改写，极端并发下可能漏吊销个别令牌；强一致场景请用 Redis `SET`/Lua 实现自己的 `IRefreshTokenStore`。
- 存储键默认带框架前缀，可用 `KeyPrefix` 隔离不同应用/环境；令牌按过期时间自动过期，无需额外清理任务。
- 切换存储不需要改业务代码：`Ledon.Security` 只依赖 `IRefreshTokenStore` 抽象。

完整文档：<https://github.com/wosledon/ledon-framework>
