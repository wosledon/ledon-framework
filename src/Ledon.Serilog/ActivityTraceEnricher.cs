using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace Ledon.Serilog;

/// <summary>
/// 把当前 Activity 的 TraceId/SpanId 富化到日志事件——日志与链路在后端（Loki/Jaeger 等）
/// 按同一 TraceId 互跳。与 OTel 日志导出互补：Serilog 结构化输出 +  trace 关联。
/// </summary>
internal sealed class ActivityTraceEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var activity = Activity.Current;
        if (activity is null)
            return;

        logEvent.AddPropertyIfAbsent(propertyFactory, "TraceId", activity.TraceId.ToString());
        logEvent.AddPropertyIfAbsent(propertyFactory, "SpanId", activity.SpanId.ToString());
    }
}
