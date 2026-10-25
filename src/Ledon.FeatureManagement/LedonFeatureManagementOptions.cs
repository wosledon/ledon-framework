namespace Ledon.FeatureManagement;

/// <summary>开关选项。</summary>
public sealed class LedonFeatureManagementOptions
{
    /// <summary>
    /// 未注册开关的默认值，默认 <c>false</c>（fail-closed：没登记的功能不开）。
    /// </summary>
    public bool DefaultEnabled { get; set; }

    /// <summary>
    /// 百分比灰度取模基数，默认 100（即按 1% 粒度）。改成 1000 可支持 0.1% 粒度。
    /// </summary>
    public int PercentageBase { get; set; } = 100;
}
