using Ledon.Imaging;
using SkiaSharp;
using Xunit;

namespace Ledon.Web.Tests;

/// <summary>图像边界：非法数据与像素上限保护。</summary>
public class ImagingEdgeTests
{
    [Fact]
    public async Task Invalid_image_data_throws_processing_exception()
    {
        var processor = new SkiaImageProcessor(new ImagingOptions());

        await Assert.ThrowsAsync<ImageProcessingException>(() =>
            processor.GetInfoAsync(new MemoryStream([1, 2, 3, 4])));
    }

    [Fact]
    public async Task Max_pixels_rejects_oversized_source()
    {
        var processor = new SkiaImageProcessor(new ImagingOptions { MaxPixels = 100 });
        var source = CreatePng(20, 20);

        await Assert.ThrowsAsync<ImageProcessingException>(() =>
            processor.GetInfoAsync(new MemoryStream(source)));
    }

    [Fact]
    public async Task Zero_target_dimension_is_rejected()
    {
        var processor = new SkiaImageProcessor(new ImagingOptions());
        var source = CreatePng(20, 20);

        await Assert.ThrowsAsync<ImageProcessingException>(() =>
            processor.ProcessAsync(new MemoryStream(source), new ImageTransform(0, 0)));
    }

    private static byte[] CreatePng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Blue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream();
        data.SaveTo(stream);
        return stream.ToArray();
    }
}
