using SkiaSharp;

namespace Ledon.Imaging;

/// <summary>SkiaSharp 图像处理器（PNG/JPEG/WebP）。</summary>
public sealed class SkiaImageProcessor(ImagingOptions options) : IImageProcessor
{
    public async Task<ImageInfo> GetInfoAsync(Stream input, CancellationToken cancellationToken = default)
    {
        using var data = await ReadDataAsync(input, cancellationToken).ConfigureAwait(false);

        using var codec = SKCodec.Create(data)
            ?? throw new ImageProcessingException("无法识别图像格式");
        Guard(codec.Info.Width, codec.Info.Height);

        return new ImageInfo(
            codec.Info.Width,
            codec.Info.Height,
            MapFormat(codec.EncodedFormat),
            data.Size);
    }

    public async Task ProcessAsync(
        Stream input,
        Stream output,
        ImageTransform transform,
        CancellationToken cancellationToken = default)
    {
        using var data = await ReadDataAsync(input, cancellationToken).ConfigureAwait(false);

        using var source = SKBitmap.Decode(data)
            ?? throw new ImageProcessingException("无法解码图像");

        Guard(source.Width, source.Height);

        var targetWidth = transform.Width ?? source.Width;
        var targetHeight = transform.Height ?? source.Height;

        if (targetWidth <= 0 || targetHeight <= 0)
            throw new ImageProcessingException("目标尺寸必须大于 0");

        if ((long)targetWidth * targetHeight > options.MaxPixels)
            throw new ImageProcessingException("目标图像像素数超过 MaxPixels");

        var format = transform.Format ?? MapFormat(SKEncodedImageFormat.Png);
        var quality = Math.Clamp(transform.Quality ?? options.DefaultQuality, 0, 100);

        using var result = Render(source, targetWidth, targetHeight, transform.Mode, format);
        using var image = SKImage.FromBitmap(result);
        using var encoded = image.Encode(ToSkiaFormat(format), quality)
            ?? throw new ImageProcessingException("图像编码失败");

        encoded.SaveTo(output);
    }

    public async Task<byte[]> ProcessAsync(
        Stream input,
        ImageTransform transform,
        CancellationToken cancellationToken = default)
    {
        await using var output = new MemoryStream();

        await ProcessAsync(input, output, transform, cancellationToken).ConfigureAwait(false);

        return output.ToArray();
    }

    private static SKBitmap Render(SKBitmap source, int width, int height, ResizeMode mode, ImageFormat format)
    {
        var target = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(target);

        if (format == ImageFormat.Jpeg)
            canvas.Clear(SKColors.White);

        var (sourceRect, destinationRect) = GetRects(source.Width, source.Height, width, height, mode);

        using var paint = new SKPaint
        {
            FilterQuality = SKFilterQuality.High,
            IsAntialias = true,
        };

        canvas.DrawBitmap(source, sourceRect, destinationRect, paint);

        return target;
    }

    private static (SKRect Source, SKRect Destination) GetRects(
        int sourceWidth,
        int sourceHeight,
        int width,
        int height,
        ResizeMode mode)
    {
        if (mode == ResizeMode.Stretch)
            return (new SKRect(0, 0, sourceWidth, sourceHeight), new SKRect(0, 0, width, height));

        if (mode == ResizeMode.Fill)
        {
            var sourceAspect = (float)sourceWidth / sourceHeight;
            var targetAspect = (float)width / height;

            if (sourceAspect > targetAspect)
            {
                var cropWidth = sourceHeight * targetAspect;
                var left = (sourceWidth - cropWidth) / 2;
                return (new SKRect(left, 0, left + cropWidth, sourceHeight), new SKRect(0, 0, width, height));
            }

            var cropHeight = sourceWidth / targetAspect;
            var top = (sourceHeight - cropHeight) / 2;
            return (new SKRect(0, top, sourceWidth, top + cropHeight), new SKRect(0, 0, width, height));
        }

        // Fit：按比例缩小/放大到目标框内的实际尺寸，居中绘制
        var scale = Math.Min((float)width / sourceWidth, (float)height / sourceHeight);
        var drawWidth = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        var drawHeight = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        var x = (width - drawWidth) / 2f;
        var y = (height - drawHeight) / 2f;

        return (new SKRect(0, 0, sourceWidth, sourceHeight), new SKRect(x, y, x + drawWidth, y + drawHeight));
    }

    private async Task<SKData> ReadDataAsync(Stream input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        await using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        return SKData.CreateCopy(buffer.ToArray());
    }

    private void Guard(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ImageProcessingException("图像尺寸无效");

        if ((long)width * height > options.MaxPixels)
            throw new ImageProcessingException($"图像像素数超过限制 {options.MaxPixels}");
    }

    private static SKEncodedImageFormat ToSkiaFormat(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => SKEncodedImageFormat.Jpeg,
        ImageFormat.Webp => SKEncodedImageFormat.Webp,
        _ => SKEncodedImageFormat.Png,
    };

    private static ImageFormat MapFormat(SKEncodedImageFormat format) => format switch
    {
        SKEncodedImageFormat.Jpeg => ImageFormat.Jpeg,
        SKEncodedImageFormat.Webp => ImageFormat.Webp,
        _ => ImageFormat.Png,
    };
}
