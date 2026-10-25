namespace Ledon.Localization;

/// <summary>文案源：按文化 + 键取文案。</summary>
public interface ILocalizationStore
{
    /// <summary>取文案；不存在返回 <c>null</c>（调用方按"键即兜底"处理）。</summary>
    string? Get(string culture, string key);

    /// <summary>该文化下全部文案（诊断/导出用）。</summary>
    IReadOnlyDictionary<string, string> GetAll(string culture);

    /// <summary>已加载的文化列表。</summary>
    IReadOnlyCollection<string> Cultures { get; }
}

/// <summary>内存文案源：代码里直接登记（小应用/测试）。</summary>
public sealed class InMemoryLocalizationStore : ILocalizationStore
{
    private readonly Dictionary<string, Dictionary<string, string>> _byCulture = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>登记（可链式）。</summary>
    public InMemoryLocalizationStore Add(string culture, IReadOnlyDictionary<string, string> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(culture);

        if (!_byCulture.TryGetValue(culture, out var map))
        {
            map = new Dictionary<string, string>(StringComparer.Ordinal);
            _byCulture[culture] = map;
        }

        foreach (var (key, value) in entries)
            map[key] = value;

        return this;
    }

    public string? Get(string culture, string key) =>
        _byCulture.TryGetValue(culture, out var map) && map.TryGetValue(key, out var value) ? value : null;

    public IReadOnlyDictionary<string, string> GetAll(string culture) =>
        _byCulture.TryGetValue(culture, out var map) ? map : new Dictionary<string, string>();

    public IReadOnlyCollection<string> Cultures => _byCulture.Keys.ToArray();
}
