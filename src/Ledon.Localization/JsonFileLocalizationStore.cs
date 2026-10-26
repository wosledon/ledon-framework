using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.Json;

namespace Ledon.Localization;

/// <summary>
/// JSON 文件文案源：<c>{ResourcesPath}/{culture}.json</c>，形如
/// <code>
/// { "Order.Created": "订单已创建", "Order.Failed": "下单失败：{Reason}" }
/// </code>
/// 文件改动即时生效：按修改时间 + 长度检测变更，只有变化时才重新解析；
/// 解析结果物化为不可变字典，避免每次取文案都读盘/解析，也不会把池化的
/// <see cref="JsonDocument"/> 长期留在内存里。坏文件保持上一份可用快照（键兜底）。
/// </summary>
public sealed class JsonFileLocalizationStore : ILocalizationStore
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly FrozenDictionary<string, string> Empty =
        FrozenDictionary<string, string>.Empty;

    private readonly string _resourcesPath;
    private readonly IReadOnlyCollection<string> _cultures;
    private readonly ConcurrentDictionary<string, Snapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);

    public JsonFileLocalizationStore(string resourcesPath, IEnumerable<string> cultures)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcesPath);
        ArgumentNullException.ThrowIfNull(cultures);

        _resourcesPath = resourcesPath;
        _cultures = cultures.ToArray();

        foreach (var culture in _cultures)
            ValidateCulture(culture);
    }

    public string? Get(string culture, string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return GetSnapshot(culture)?.Entries.GetValueOrDefault(key);
    }

    public IReadOnlyDictionary<string, string> GetAll(string culture) =>
        GetSnapshot(culture)?.Entries ?? Empty;

    public IReadOnlyCollection<string> Cultures => _cultures;

    private Snapshot? GetSnapshot(string culture)
    {
        ValidateCulture(culture);

        var path = System.IO.Path.Combine(_resourcesPath, $"{culture}.json");

        DateTime lastWriteTimeUtc;
        long length;

        try
        {
            var info = new FileInfo(path);

            if (!info.Exists)
            {
                // 文件被删：缓存同步失效，下一次创建新文件会重新加载
                _snapshots.TryRemove(culture, out _);
                return null;
            }

            lastWriteTimeUtc = info.LastWriteTimeUtc;
            length = info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return _snapshots.TryGetValue(culture, out var cached) ? cached : null;
        }

        if (_snapshots.TryGetValue(culture, out var snapshot)
            && snapshot.LastWriteTimeUtc == lastWriteTimeUtc
            && snapshot.Length == length)
        {
            return snapshot;
        }

        try
        {
            // 先整体读入再解析：避免和写入方交错读到半截 JSON
            var json = System.IO.File.ReadAllText(path);

            using var document = JsonDocument.Parse(json, DocumentOptions);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return snapshot;   // 顶层不是对象：视为坏文件，保留上一份

            var entries = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var property in document.RootElement.EnumerateObject())
            {
                // JSON 允许重复键：后出现的覆盖先出现的（与常规解析器一致，不抛异常）
                if (property.Value.ValueKind == JsonValueKind.String)
                    entries[property.Name] = property.Value.GetString()!;
            }

            var fresh = new Snapshot(
                lastWriteTimeUtc,
                length,
                entries.ToFrozenDictionary(StringComparer.Ordinal));

            _snapshots[culture] = fresh;

            return fresh;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 坏文件 / 正在写入：保持上一份快照（键兜底），下次读取继续重试
            return snapshot;
        }
    }

    /// <summary>文化名只允许文件名安全字符：防止 <c>../</c> 之类穿越到资源目录外。</summary>
    private static void ValidateCulture(string culture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(culture);

        if (culture is "." or ".."
            || culture.Contains('/')
            || culture.Contains('\\')
            || culture.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"非法的文化名：{culture}", nameof(culture));
        }
    }

    private sealed record Snapshot(
        DateTime LastWriteTimeUtc,
        long Length,
        FrozenDictionary<string, string> Entries);
}
