using Ledon.BlobStorage;
using Ledon.Imaging;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;

namespace Ledon.Web.Tests;

/// <summary>图像：元信息、缩放/格式转换与文件存储缩略图。</summary>
public class ImagingTests
{
    [Fact]
    public async Task Reads_info_and_fits_image_into_target()
    {
        var processor = new SkiaImageProcessor(new ImagingOptions());
        var source = CreatePng(100, 50);

        var info = await processor.GetInfoAsync(new MemoryStream(source));
        Assert.Equal(100, info.Width);
        Assert.Equal(50, info.Height);
        Assert.Equal(ImageFormat.Png, info.Format);

        var resized = await processor.ProcessAsync(
            new MemoryStream(source),
            new ImageTransform(20, 20, ResizeMode.Fit, ImageFormat.Png));

        using var bitmap = SKBitmap.Decode(resized);
        Assert.Equal(20, bitmap.Width);
        Assert.Equal(20, bitmap.Height);
        Assert.NotEqual(SKColors.Transparent, bitmap.GetPixel(10, 10));
    }

    [Fact]
    public async Task Fill_crops_and_converts_to_jpeg()
    {
        var processor = new SkiaImageProcessor(new ImagingOptions());
        var source = CreatePng(100, 50);

        var filled = await processor.ProcessAsync(
            new MemoryStream(source),
            new ImageTransform(20, 20, ResizeMode.Fill, ImageFormat.Png));
        using (var bitmap = SKBitmap.Decode(filled))
        {
            Assert.Equal(20, bitmap.Width);
            Assert.Equal(20, bitmap.Height);
        }

        var jpeg = await processor.ProcessAsync(
            new MemoryStream(source),
            new ImageTransform(Format: ImageFormat.Jpeg, Quality: 80));

        Assert.Equal(0xFF, jpeg[0]);
        Assert.Equal(0xD8, jpeg[1]);
    }

    [Fact]
    public async Task Storage_service_writes_thumbnail()
    {
        var storage = new InMemoryFileStorage(new LedonBlobStorageOptions());
        var processor = new SkiaImageProcessor(new ImagingOptions());
        var service = new ImageStorageService(storage, processor);
        var source = CreatePng(80, 40);

        await using (var stream = new MemoryStream(source))
            await storage.SaveAsync("images/source.png", stream);

        var saved = await service.CreateThumbnailAsync("images/source.png", "images/thumb.png", 32, 32);

        Assert.Equal("image/png", saved.ContentType);
        Assert.True(await storage.ExistsAsync("images/thumb.png"));

        await using var thumbnail = await storage.OpenReadAsync("images/thumb.png");
        using var bitmap = SKBitmap.Decode(thumbnail);
        Assert.Equal(32, bitmap.Width);
        Assert.Equal(32, bitmap.Height);
    }

    [Fact]
    public void Di_registration_resolves_processor()
    {
        var services = new ServiceCollection();
        services.AddLedonImaging();

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IImageProcessor>());
        Assert.NotNull(provider.GetRequiredService<ImagingOptions>());
    }

    private static byte[] CreatePng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Red);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream();
        data.SaveTo(stream);

        return stream.ToArray();
    }
}
