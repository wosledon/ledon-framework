namespace Ledon.Observability;

/// <summary>可观测性默认参数。未指定 OTLP 端点时读取标准 OTEL_EXPORTER_OTLP_ENDPOINT 环境变量。</summary>
public sealed class LedonObservabilityOptions
{
    /// <summary>服务名（链路/指标资源标识），默认取入口程序集名。</summary>
    public string ServiceName { get; set; } =
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "ledon-app";

    /// <summary>服务版本，默认 1.0.0。</summary>
    public string ServiceVersion { get; set; } = "1.0.0";

    /// <summary>启用链路追踪，默认开。</summary>
    public bool EnableTracing { get; set; } = true;

    /// <summary>启用指标，默认开。</summary>
    public bool EnableMetrics { get; set; } = true;

    /// <summary>
    /// 启用日志导出（默认开）：ILogger 日志经 OTLP 导出（含日志作用域，如关联 ID），
    /// 与链路/指标共用同一端点与资源。不需要日志导出时可关掉减轻出口压力。
    /// </summary>
    public bool EnableLogs { get; set; } = true;

    /// <summary>OTLP 导出端点（如 http://localhost:4317）；null 时走环境变量。</summary>
    public Uri? OtlpEndpoint { get; set; }

    /// <summary>
    /// 额外使用控制台导出器（默认关）——开发/排障时把 span 与指标直接打到控制台，
    /// 无需 Jaeger/Tempo 等基础设施即可看到框架内部链路。
    /// </summary>
    public bool UseConsoleExporter { get; set; }
}
