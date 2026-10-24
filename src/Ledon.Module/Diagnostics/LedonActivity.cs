using System.Diagnostics;

namespace Ledon.Module.Diagnostics;

/// <summary>
/// 框架自身的链路源（OpenTelemetry ActivitySource）。框架的核心机制——模块生命周期、
/// 事件发布、队列处理——都会以 span 形式发出，"框架帮你做了什么"因此全程可观测。
/// 接入方式：AddLedonObservability() 会自动监听这些源；也可自行 AddSource("Ledon")。
/// </summary>
public static class LedonActivity
{
    /// <summary>总源名（监听它即收听框架所有 span）。</summary>
    public const string RootSourceName = "Ledon";

    /// <summary>模块生命周期（ConfigureServices / Initialize / Shutdown）。</summary>
    public const string ModuleSourceName = $"{RootSourceName}.Module";

    /// <summary>事件发布。</summary>
    public const string EventSourceName = $"{RootSourceName}.Events";

    /// <summary>后台队列处理。</summary>
    public const string QueueSourceName = $"{RootSourceName}.Queues";

    /// <summary>模块生命周期 ActivitySource。</summary>
    public static ActivitySource Modules { get; } = new(ModuleSourceName);

    /// <summary>事件 ActivitySource。</summary>
    public static ActivitySource Events { get; } = new(EventSourceName);

    /// <summary>后台队列 ActivitySource。</summary>
    public static ActivitySource Queues { get; } = new(QueueSourceName);
}
