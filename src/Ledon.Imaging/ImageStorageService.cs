using Ledon.BlobStorage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ledon.Imaging;

/// <summary>文件存储中的图像处理（缩略图/格式转换）。</summary>
public sealed class ImageStorageService(IFileStorage storage, IImageProcessor processor)
{
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
    };

    public async Task<ImageInfo> GetInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var input = await storage.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        return await processor.GetInfoAsync(input, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BlobInfo> CreateThumbnailAsync(
        string sourcePath,
        string targetPath,
        int maxWidth,
        int maxHeight,
        ImageFormat format = ImageFormat.Png,
        int? quality = null,
        CancellationToken cancellationToken = default)
    {
        await using var input = await storage.OpenReadAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        await using var output = new MemoryStream();

        await processor.ProcessAsync(
            input,
            output,
            new ImageTransform(maxWidth, maxHeight, ResizeMode.Fit, format, quality),
            cancellationToken).ConfigureAwait(false);

        output.Position = 0;

        await storage.SaveAsync(
            targetPath,
            output,
            new BlobUploadOptions
            {
                ContentType = ContentTypes.GetValueOrDefault(Path.GetExtension(targetPath), "application/octet-stream"),
                Overwrite = true,
            },
            cancellationToken).ConfigureAwait(false);

        return await storage.GetInfoAsync(targetPath, cancellationToken).ConfigureAwait(false)
            ?? throw new ImageProcessingException($"缩略图保存失败：{targetPath}");
    }

    public async Task<BlobInfo> ConvertAsync(
        string sourcePath,
        string targetPath,
        ImageFormat format,
        int? quality = null,
        CancellationToken cancellationToken = default)
    {
        await using var input = await storage.OpenReadAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        await using var output = new MemoryStream();

        await processor.ProcessAsync(
            input,
            output,
            new ImageTransform(Format: format, Quality: quality),
            cancellationToken).ConfigureAwait(false);

        output.Position = 0;

        await storage.SaveAsync(
            targetPath,
            output,
            new BlobUploadOptions
            {
                ContentType = ContentTypes.GetValueOrDefault(Path.GetExtension(targetPath), "application/octet-stream"),
                Overwrite = true,
            },
            cancellationToken).ConfigureAwait(false);

        return await storage.GetInfoAsync(targetPath, cancellationToken).ConfigureAwait(false)
            ?? throw new ImageProcessingException($"图像保存失败：{targetPath}");
    }
}

/// <summary>图像模块注册。</summary>
public static class LedonImagingServiceCollectionExtensions
{
    public static IServiceCollection AddLedonImaging(
        this IServiceCollection services,
        Action<ImagingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ImagingOptions();
        configure?.Invoke(options);

        if (options.MaxPixels <= 0)
            throw new InvalidOperationException("ImagingOptions.MaxPixels 必须大于 0");

        if (options.DefaultQuality is < 0 or > 100)
            throw new InvalidOperationException("ImagingOptions.DefaultQuality 必须在 0-100 之间");

        services.AddSingleton(options);
        services.TryAddSingleton<IImageProcessor, SkiaImageProcessor>();
        services.TryAddSingleton<ImageStorageService>();

        return services;
    }
}
