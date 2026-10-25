namespace Ledon.FeatureManagement;

/// <summary>灰度判定的主体（用于按租户/用户/百分比定向）。</summary>
public readonly record struct FeatureTarget(string? TenantId, string? UserId, string? SessionId)
{
    /// <summary>用于百分比分桶的稳定标识（优先级：会话 &gt; 用户 &gt; 租户；都没有时退化为机器标识）。</summary>
    public string BucketKey =>
        SessionId ?? UserId ?? TenantId ?? Environment.MachineName;
}

/// <summary>
/// 功能开关判定。业务/端点只问"开没开"，不关心开关从哪来（配置中心、运行时存储、默认值）：
/// <code>
/// if (await features.IsEnabledAsync("new-checkout", ct)) { ... }
/// </code>
/// </summary>
public interface IFeatureManager
{
    /// <summary>当前已注册的开关名（诊断/管理端用）。</summary>
    IReadOnlyCollection<string> Features { get; }

    /// <summary>是否启用。未注册的开关返回 <c>false</c>（fail-closed，新功能默认关）。</summary>
    bool IsEnabled(string feature, FeatureTarget target = default);

    /// <summary><see cref="IsEnabled"/> 的异步版本（存储可能来自远端）。</summary>
    ValueTask<bool> IsEnabledAsync(string feature, FeatureTarget target = default, CancellationToken cancellationToken = default);

    /// <summary>运行时改开关值（管理端操作用；配置来源的开关以配置为准）。</summary>
    void SetEnabled(string feature, bool enabled);
}
