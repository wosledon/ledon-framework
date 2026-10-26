# Ledon.Imaging

基于 SkiaSharp（MIT）的图像处理：尺寸读取、等比缩放/裁剪、缩略图、格式转换，并可读写 `IFileStorage`。

```csharp
builder.Services.AddLedonImaging(o => o.MaxPixels = 40_000_000);

var image = await processor.GetInfoAsync(stream);           // 宽高/格式/字节数
var bytes = await processor.ProcessAsync(stream, new ImageTransform(
    Width: 320, Height: 320, Mode: ResizeMode.Fit, Format: ImageFormat.Webp, Quality: 80));
```

```csharp
public class AvatarService(IImageProcessor images, IFileStorage storage)
{
    public Task<BlobInfo> CreateAsync(string source, string target, CancellationToken ct) =>
        new ImageStorageService(storage, images)
            .CreateThumbnailAsync(source, target, 128, 128, ImageFormat.Png, ct: ct);
}
```

- 支持 PNG / JPEG / WebP；JPEG 透明背景自动填白；
- `Fit` 等比放入、`Fill` 等比裁剪、`Stretch` 拉伸；
- `MaxPixels` 防止超大图/解压炸弹；
- 原生库按平台引入：`SkiaSharp.NativeAssets.Win32` / `.Linux` / `.macOS`（框架只引用 SkiaSharp 本身）；
- `ImageStorageService` 读取源对象、处理、写回目标对象并返回 `BlobInfo`。

完整文档：<https://github.com/wosledon/ledon-framework>
