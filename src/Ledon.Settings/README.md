# Ledon.Settings

运行时设置：强类型读写、JSON 序列化、可选 Data Protection 加密、JSON 文件持久化。

```csharp
builder.Services.AddLedonSettings(o =>
{
    o.StorePath = "App_Data/settings.json";   // 留空 = 进程内存储
});

public class SmtpSettingsService(ISettingsService settings)
{
    public Task<SmtpOptions?> GetAsync(CancellationToken ct) =>
        settings.GetAsync<SmtpOptions>("smtp", ct);

    public Task SaveAsync(SmtpOptions options, CancellationToken ct) =>
        settings.SetAsync("smtp", options, encrypted: true, updatedBy: "admin", ct);
}
```

- 值以 JSON 保存，支持任意可序列化类型；
- `encrypted: true` 使用 ASP.NET Core Data Protection 加密（密文带 `dp:v1:` 前缀）；
- 默认内存存储；配置 `StorePath` 后落 JSON 文件，写入先临时文件再原子替换；
- 多实例请替换 `ISettingsStore` 为共享数据库实现；
- 加密设置跨实例/重启可读的前提是 Data Protection 密钥环共享（Redis/文件系统）。

完整文档：<https://github.com/wosledon/ledon-framework>
