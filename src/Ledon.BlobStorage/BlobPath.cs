namespace Ledon.BlobStorage;

/// <summary>路径规则：规范化、扩展名/大小校验、租户前缀。</summary>
internal static class BlobPath
{
    /// <summary>规范化：统一正斜杠、去首尾斜杠与 <c>./</c>、拒绝对抗路径。</summary>
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var normalized = path.Replace('\\', '/').Trim();

        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];

        normalized = normalized.Trim('/');

        if (normalized.Length == 0)
            throw new ArgumentException("路径不能为空", nameof(path));

        if (normalized.Split('/').Any(segment => segment is ".." or "." or ""))
            throw new ArgumentException($"路径含非法段：{path}", nameof(path));

        return normalized;
    }

    /// <summary>按选项校验扩展名白名单。</summary>
    public static void ValidateExtension(string path, LedonBlobStorageOptions options)
    {
        if (options.AllowedExtensions.Count == 0)
            return;

        var extension = System.IO.Path.GetExtension(path);

        if (!options.AllowedExtensions.Contains(extension))
            throw new InvalidOperationException($"不允许的文件类型 {extension}（允许：{string.Join(", ", options.AllowedExtensions)}）");
    }

    /// <summary>基础校验（长度 + 非法段）。</summary>
    public static void Validate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (path.Length > 512)
            throw new ArgumentException("路径过长（>512 字符）", nameof(path));

        Normalize(path);
    }

    /// <summary>加租户前缀（未启用多租户或无租户上下文时原样返回）。</summary>
    public static string WithTenantPrefix(string path, Func<string?> tenantAccessor)
    {
        var normalized = Normalize(path);
        var tenant = tenantAccessor();

        return string.IsNullOrEmpty(tenant) ? normalized : $"tenants/{tenant}/{normalized}";
    }
}

/// <summary>扩展名 → MIME 的常用映射（缺省 application/octet-stream）。</summary>
internal static class ContentTypes
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".svg"] = "image/svg+xml",
        [".pdf"] = "application/pdf",
        [".json"] = "application/json",
        [".xml"] = "text/xml",
        [".txt"] = "text/plain",
        [".csv"] = "text/csv",
        [".zip"] = "application/zip",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".mp4"] = "video/mp4",
        [".mp3"] = "audio/mpeg",
    };

    public static string Guess(string path) =>
        Map.TryGetValue(System.IO.Path.GetExtension(path), out var type) ? type : "application/octet-stream";
}
