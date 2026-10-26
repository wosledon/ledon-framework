# Ledon.Data.Encryption

EF Core 列级透明加密：标注 `[Encrypted]` 的字符串属性落库自动加密、读取自动解密。

```csharp
builder.Services.AddLedonDataEncryption();

public class AppDbContext(DbContextOptions<AppDbContext> options, IEncryptionService encryption)
    : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder builder) =>
        builder.ApplyEncryption(encryption);
}

public class Customer
{
    public int Id { get; set; }

    [Encrypted]
    public string IdCard { get; set; } = "";
}
```

- 基于 ASP.NET Core Data Protection，密钥轮换/共享密钥环由 DP 负责；
- 密文带 `enc:v1:` 前缀；无前缀的历史数据按明文读取，便于平滑迁移；
- 只支持 `string` 属性；非 string 标注会在建模时直接报错；
- 加密列通常无法做等值查询/索引，按业务需要保留检索列（如哈希列）。

完整文档：<https://github.com/wosledon/ledon-framework>
