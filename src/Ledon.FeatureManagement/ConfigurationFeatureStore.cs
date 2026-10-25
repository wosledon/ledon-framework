using Microsoft.Extensions.Configuration;

namespace Ledon.FeatureManagement;

/// <summary>单个开关的定义。</summary>
public sealed class FeatureDefinition
{
    /// <summary>开关名。</summary>
    public required string Name { get; init; }

    /// <summary>布尔开关（直接开/关）。</summary>
    public bool? Enabled { get; init; }

    /// <summary>按租户白名单开启（逗号分隔或字符串数组）。</summary>
    public IReadOnlyCollection<string> Tenants { get; init; } = [];

    /// <summary>按用户白名单开启。</summary>
    public IReadOnlyCollection<string> Users { get; init; } = [];

    /// <summary>百分比灰度 0..<see cref="LedonFeatureManagementOptions.PercentageBase"/>。</summary>
    public int? Percentage { get; init; }
}

/// <summary>
/// 从配置读取开关定义（<c>Features</c> 段），支持配置中心热更新：
/// <code>
/// "Features": {
///   "new-checkout": true,                                  // 直接开
///   "beta-report":  { "Percentage": 20 },                  // 20% 灰度
///   "vip-only":     { "Tenants": [ "t-001", "t-002" ] }    // 按租户白名单
/// }
/// </code>
/// </summary>
public sealed class ConfigurationFeatureStore
{
    private readonly IConfiguration _configuration;
    private readonly LedonFeatureManagementOptions _options;
    private readonly Dictionary<string, FeatureDefinition> _overrides = new(StringComparer.OrdinalIgnoreCase);

    public ConfigurationFeatureStore(IConfiguration configuration, LedonFeatureManagementOptions options)
    {
        _configuration = configuration;
        _options = options;
    }

    /// <summary>当前生效的全部开关定义（配置 + 运行时覆盖）。</summary>
    public IReadOnlyCollection<FeatureDefinition> Snapshot()
    {
        var result = new Dictionary<string, FeatureDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var section in _configuration.GetSection("Features").GetChildren())
            if (TryParse(section.Key, section, out var definition))
                result[definition.Name] = definition;

        foreach (var (name, definition) in _overrides)
            result[name] = definition;

        return result.Values.OrderBy(definition => definition.Name, StringComparer.Ordinal).ToArray();
    }

    /// <summary>读取单个开关的最新值（配置变化即时可见，无需重启）。</summary>
    public FeatureDefinition? Get(string feature)
    {
        if (_overrides.TryGetValue(feature, out var overridden))
            return overridden;

        var section = _configuration.GetSection("Features").GetSection(feature);
        return TryParse(feature, section, out var definition) ? definition : null;
    }

    /// <summary>运行时覆盖（管理端用）。配置里存在同名开关时，再次 SetEnabled 可改回。</summary>
    public void SetEnabled(string feature, bool enabled) =>
        _overrides[feature] = new FeatureDefinition { Name = feature, Enabled = enabled };

    private bool TryParse(string name, IConfigurationSection section, out FeatureDefinition definition)
    {
        definition = null!;

        var raw = section.Value;
        if (string.IsNullOrWhiteSpace(raw) && !section.Exists())
            return false;

        // 简写：布尔值即开关
        if (bool.TryParse(raw, out var enabled))
        {
            definition = new FeatureDefinition { Name = name, Enabled = enabled };
            return true;
        }

        // 完整写法：对象
        var definition1 = new FeatureDefinition
        {
            Name = name,
            Enabled = section.GetValue<bool?>("Enabled"),
            Percentage = section.GetValue<int?>("Percentage"),
            Tenants = section.GetSection("Tenants").Get<string[]>() ?? [],
            Users = section.GetSection("Users").Get<string[]>() ?? [],
        };

        definition = definition1;
        return true;
    }

    /// <summary>百分比灰度：稳定分桶（同一主体永远落在同一桶，开关比例不变则判定不变）。</summary>
    public bool InPercentageBucket(FeatureTarget target, FeatureDefinition definition)
    {
        if (definition.Percentage is not { } percentage)
            return false;

        if (percentage <= 0)
            return false;

        if (percentage >= _options.PercentageBase)
            return true;

        var hash = System.HashCode.Combine(definition.Name, target.BucketKey);
        var bucket = (uint)hash % (uint)_options.PercentageBase;

        return bucket < (uint)percentage;
    }
}
