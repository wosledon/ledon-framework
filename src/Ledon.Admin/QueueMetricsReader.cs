using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Ledon.Queues;

namespace Ledon.Admin;

/// <summary>
/// 指标读取器：订阅框架自己的 <see cref="QueueMetrics.Meter"/>，记录各计数器最新值，
/// 让运维端点能回答"累计投递多少、失败多少"（OTel/Prometheus 之外的第二条读取路径）。
/// </summary>
public sealed class QueueMetricsReader : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentDictionary<string, long> _values = new(StringComparer.Ordinal);

    /// <summary>启动订阅（幂等）。</summary>
    public QueueMetricsReader Start()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == QueueMetrics.MeterName && instrument is Counter<long>)
                listener.EnableMeasurementEvents(instrument, this);   // state = 本实例
        };

        // .NET 9+ 的 MeasurementCallback 带 state 参数：从 state 取回实例
        _listener.SetMeasurementEventCallback<long>(static (instrument, measurement, tags, state) =>
            ((QueueMetricsReader)state!).OnMeasurement(instrument, measurement));

        _listener.Start();

        return this;
    }

    /// <summary>取指定计数器当前值（没收到过测量返回 0）。</summary>
    public long Value(string name) => _values.TryGetValue(name, out var value) ? value : 0;

    /// <summary>全部已观测计数器。</summary>
    public IReadOnlyDictionary<string, long> Snapshot() => new Dictionary<string, long>(_values);

    /// <summary>停止订阅（进程退出时调用；不调用也只多一个监听器）。</summary>
    public void Dispose() => _listener.Dispose();

    private void OnMeasurement(Instrument instrument, long measurement)
    {
        // 计数器单调累加：按"指标名"累计（带 tag 的同名指标取其和）
        _values.AddOrUpdate(instrument.Name, measurement, (_, current) => current + measurement);
    }
}
