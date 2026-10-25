using System.Text.Json;

namespace Ledon.Localization;

/// <summary>
/// JSON 文件文案源：<c>{ResourcesPath}/{culture}.json</c>，形如
/// <code>
/// { "Order.Created": "订单已创建", "Order.Failed": "下单失败：{Reason}" }
/// </code>
/// 文件改动即时生效（每次读取重新加载，无缓存），与配置中心/挂载卷配合可热更新。
/// </summary>
public sealed class JsonFileLocalizationStore : ILocalizationStore
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private readonly string _resourcesPath;
    private readonly IReadOnlyCollection<string> _cultures;

    public JsonFileLocalizationStore(string resourcesPath, IEnumerable<string> cultures)
    {
        _resourcesPath = resourcesPath;
        _cultures = cultures.ToArray();
    }

    public string? Get(string culture, string key)
    {
        var document = Load(culture);

        return document is not null
            && document.RootElement.TryGetProperty(key, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    public IReadOnlyDictionary<string, string> GetAll(string culture)
    {
        var document = Load(culture);

        if (document is null)
            return new Dictionary<string, string>();

        return document.RootElement.EnumerateObject()
            .Where(property => property.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(property => property.Name, property => property.Value.GetString()!);
    }

    public IReadOnlyCollection<string> Cultures => _cultures;

    private JsonDocument? Load(string culture)
    {
        var path = System.IO.Path.Combine(_resourcesPath, $"{culture}.json");

        if (!System.IO.File.Exists(path))
            return null;

        try
        {
            return JsonDocument.Parse(System.IO.File.ReadAllText(path), DocumentOptions);
        }
        catch (JsonException)
        {
            // 坏文件不当异常冒到请求管道：视为无文案（键兜底），启动时可由诊断接口发现
            return null;
        }
    }
}
