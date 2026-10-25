# Ledon.BlobStorage

文件/对象存储抽象：本地磁盘、内存（测试）、S3 兼容（`Ledon.BlobStorage.S3`，MinIO/AWS）可替换，业务只依赖 `IFileStorage`。

```csharp
builder.Services.AddLedonBlobStorage(o =>
{
    o.Provider = LedonBlobProvider.Local;
    o.RootPath = "App_Data/blobs";
    o.AllowedExtensions.Add(".png");     // 类型白名单（空 = 不限制）
});

public class AvatarService(IFileStorage storage)
{
    public async Task<string> UploadAsync(Stream content, CancellationToken ct = default)
    {
        var path = $"avatars/{Guid.NewGuid():N}.png";
        await storage.SaveAsync(path, content, ct: ct);
        return path;
    }
}
```

- 路径即主键：`SaveAsync` / `OpenReadAsync` / `DeleteAsync` / `ExistsAsync` / `GetInfoAsync` / `ListAsync`；
- 安全：路径规范化 + 防目录穿越；默认拒绝覆盖（`Overwrite = true` 才覆盖）；大小上限默认 100MB；
- 多租户：默认按 `tenants/{tenantId}/` 自动加前缀，租户间文件互相不可见（`o.TenantPathPrefix = false` 关闭）；
- 本地实现按文件粒度异步锁，并发写不会半写半读；删除后清理空目录；
- S3/MinIO：引用 `Ledon.BlobStorage.S3`，`AddLedonS3BlobStorage(...)`，同一接口不变。

完整文档：<https://github.com/wosledon/ledon-framework>
