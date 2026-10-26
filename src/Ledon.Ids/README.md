# Ledon.Ids

分布式 ID 生成：ULID、GUID v7、Snowflake，多实例部署通过 `WorkerId` 避免冲突。

```csharp
builder.Services.AddLedonIds(o => o.WorkerId = 1);   // 每个实例唯一（0-1023）

public class OrderService(IIdGenerator ids)
{
    public string NewOrderId() => ids.NewId();                 // ULID，可按时间排序
    public long NewSequence() => ids.NewSnowflake();            // 64 位趋势递增
    public Guid NewGuid() => ids.NewGuid();                     // GUID v7
}
```

- `Ulid.New()`：48 位时间戳 + 80 位随机，同毫秒单调递增，26 位 Crockford Base32；
- `Guid.CreateVersion7()`：时间有序 GUID；
- Snowflake：时间戳 + workerId + 12 位序列，单节点趋势递增；
- 所有生成器线程安全。

完整文档：<https://github.com/wosledon/ledon-framework>
