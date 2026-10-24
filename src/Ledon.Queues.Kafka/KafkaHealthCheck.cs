using Confluent.Kafka;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ledon.Queues.Kafka;

/// <summary>
/// Kafka 连通性健康检查（配了传输入口时由 <c>AddLedonKafkaTransport</c> 自动登记）。
/// 用 AdminClient 取集群元数据验证 broker 可服务；连接惰性建立，broker 短暂不在线不阻塞启动。
/// </summary>
internal sealed class KafkaHealthCheck : IHealthCheck
{
    private readonly KafkaOptions _options;

    public KafkaHealthCheck(KafkaOptions options)
    {
        _options = options;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var admin = new AdminClientBuilder(new AdminClientConfig
            {
                BootstrapServers = _options.BootstrapServers,
            }).Build();

            var metadata = admin.GetMetadata(TimeSpan.FromSeconds(5));
            return HealthCheckResult.Healthy(
                $"Kafka 集群正常（{metadata.Brokers.Count} 个 broker）");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Kafka 不可用", ex);
        }
    }
}
