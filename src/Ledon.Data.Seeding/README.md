# Ledon.Data.Seeding

模块化数据播种：实现 `IDataSeeder` 即被自动发现，在数据库结构就绪后按 `Order` 顺序执行。

```csharp
builder.Services.AddLedonData();
builder.Services.AddLedonDataSeeding(o =>
{
    o.FailFast = true;   // 播种失败是否阻止启动
});

public class AdminSeeder(AppDbContext db) : IDataSeeder
{
    public int Order => 10;

    public async Task SeedAsync(CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(u => u.Name == "admin", ct))
        {
            db.Users.Add(new User { Name = "admin" });
            await db.SaveChangesAsync(ct);
        }
    }
}
```

- 播种器以 Scoped 注册，可直接注入 DbContext/仓储；
- 按 `Order` 升序执行，适合"字典 → 管理员 → 演示数据"的依赖顺序；
- 每次启动都会执行，**播种逻辑必须幂等**（先查后插/upsert）；
- 在 `AddLedonData()` 之后调用，确保晚于建库/迁移。

完整文档：<https://github.com/wosledon/ledon-framework>
