namespace Ledon.Imaging;

/// <summary>图像格式。</summary>
public enum ImageFormat
{
    Png,
    Jpeg,
    Webp,
}

/// <summary>缩放模式。</summary>
public enum ResizeMode
{
    /// <summary>等比缩放，完整放入目标框（可能留边）。</summary>
    Fit,

    /// <summary>等比缩放并居中裁剪到目标尺寸。</summary>
    Fill,

    /// <summary>拉伸到目标尺寸（不保持比例）。</summary>
    Stretch,
}

/// <summary>图像处理选项。</summary>
public sealed class ImagingOptions
{
    /// <summary>最大像素数（宽×高），默认 4000 万，防解压炸弹。</summary>
    public long MaxPixels { get; set; } = 40_000_000;

    /// <summary>JPEG/WebP 默认质量（0-100），默认 85。</summary>
    public int DefaultQuality { get; set; } = 85;
}

/// <summary>图像元信息。</summary>
public sealed record ImageInfo(int Width, int Height, ImageFormat Format, long SizeBytes);

/// <summary>图像变换参数。</summary>
public sealed record ImageTransform(
    int? Width = null,
    int? Height = null,
    ResizeMode Mode = ResizeMode.Fit,
    ImageFormat? Format = null,
    int? Quality = null);

/// <summary>图像处理失败。</summary>
public sealed class ImageProcessingException(string message) : Exception(message);

/// <summary>图像处理。</summary>
public interface IImageProcessor
{
    Task<ImageInfo> GetInfoAsync(Stream input, CancellationToken cancellationToken = default);

    Task ProcessAsync(
        Stream input,
        Stream output,
        ImageTransform transform,
        CancellationToken cancellationToken = default);

    Task<byte[]> ProcessAsync(
        Stream input,
        ImageTransform transform,
        CancellationToken cancellationToken = default);
}
