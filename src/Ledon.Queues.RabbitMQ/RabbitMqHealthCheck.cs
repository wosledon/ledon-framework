using Microsoft.Extensions.Diagnostics.HealthChecks;
using global::RabbitMQ.Client;

namespace Ledon.Queues.RabbitMQ;

/// <summary>
/// RabbitMQ 连通性健康检查（配了传输入口时由 <c>AddLedonRabbitMqTransport</c> 自动登记）。
/// broker 挂了 → 就绪探针 503 摘流；存活探针仍 200 不重启。
/// </summary>
internal sealed class RabbitMqHealthCheck : IHealthCheck
{
    private readonly IConnection _connection;

    public RabbitMqHealthCheck(IConnection connection)
    {
        _connection = connection;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_connection.IsOpen)
                return Task.FromResult(HealthCheckResult.Unhealthy("RabbitMQ 连接未打开"));

            // 开一条临时通道验证 broker 可服务（用完即关）
            var channel = _connection.CreateChannelAsync(cancellationToken: cancellationToken).GetAwaiter().GetResult();
            try
            {
                return Task.FromResult(HealthCheckResult.Healthy("RabbitMQ 连接正常"));
            }
            finally
            {
                channel.Dispose();
            }
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("RabbitMQ 不可用", ex));
        }
    }
}
