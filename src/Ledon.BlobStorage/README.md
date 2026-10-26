# Ledon.BlobStorage

文件/对象存储抽象：本地磁盘、内存（测试）、S3 兼容对象存储（AWS S3 / MinIO），业务只依赖 `IFileStorage`。

```csharp
builder.Services.AddLedonBlobStorage(o =>
{
    o.Provider = LedonBlobProvider.Local;
    o.RootPath = "App_Data/blobs";
    o.AllowedExtensions.Add(".png");     // 类型白名单（空 = 不限制）
});

// S3 / MinIO
builder.Services.AddLedonBlobStorage(o =>
{
    o.Provider = LedonBlobProvider.S3;
    o.ServiceUrl = "http://localhost:9000";   // AWS 官方 S3 可留空
    o.BucketName = "uploads";
    o.AccessKey = "...";
    o.SecretKey = "...";
});
```

- 路径即主键：`SaveAsync` / `OpenReadAsync` / `DeleteAsync` / `ExistsAsync` / `GetInfoAsync` / `ListAsync`；
- 安全：路径规范化 + 防目录穿越；默认拒绝覆盖（`Overwrite = true` 才覆盖）；大小上限默认 100MB；
- 本地实现先写临时文件再原子替换，失败不会留下半截文件；锁按固定分片复用，不会随文件数增长；
- 多租户：默认按 `tenants/{tenantId}/` 自动加前缀，租户间文件互相不可见；列表同样限定当前租户；
- S3：路径风格（MinIO 必需）、自动建桶、`If-None-Match: *` 防并发覆盖；未配密钥时走 AWS 默认凭证链。

完整文档：<https://github.com/wosledon/ledon-framework>