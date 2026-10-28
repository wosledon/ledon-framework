using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Testcontainers.Kafka;
using Testcontainers.Minio;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace Ledon.Testing.Containers;

/// <summary>
/// 集成测试容器夹具：Redis / PostgreSQL / RabbitMQ / Kafka / MinIO。
/// 需要本机 Docker；无 Docker 环境可先调用 <see cref="DockerAvailableAsync"/> 跳过。
/// </summary>
public static class LedonContainers
{
    /// <summary>检查 Docker 是否可用（docker info）。</summary>
    public static async Task<bool> DockerAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", "info")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (process is null)
                return false;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            or OperationCanceledException
            or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>Redis 夹具。</summary>
public sealed class RedisFixture : IAsyncDisposable
{
    private readonly RedisContainer _container;

    private RedisFixture(RedisContainer container) => _container = container;

    /// <summary>StackExchange.Redis 连接串。</summary>
    public string ConnectionString => _container.GetConnectionString();

    /// <summary>启动容器（镜像 redis:7-alpine）。</summary>
    [ExcludeFromCodeCoverage(Justification = "需要 Docker；由 CI 集成测试覆盖，单元测试环境不计入覆盖率。")]
    public static async Task<RedisFixture> StartAsync(CancellationToken cancellationToken = default)
    {
        var container = new RedisBuilder("redis:7-alpine").Build();
        await container.StartAsync(cancellationToken).ConfigureAwait(false);

        return new RedisFixture(container);
    }

    [ExcludeFromCodeCoverage(Justification = "需要 Docker；由 CI 集成测试覆盖，单元测试环境不计入覆盖率。")]
    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

/// <summary>PostgreSQL 夹具。</summary>
public sealed class PostgreSqlFixture : IAsyncDisposable
{
    private readonly PostgreSqlContainer _container;

    private PostgreSqlFixture(PostgreSqlContainer container) => _container = container;

    /// <summary>Npgsql 连接串。</summary>
    public string ConnectionString => _container.GetConnectionString();

    [ExcludeFromCodeCoverage(Justification = "需要 Docker；由 CI 集成测试覆盖，单元测试环境不计入覆盖率。")]
    public static async Task<PostgreSqlFixture> StartAsync(CancellationToken cancellationToken = default)
    {
        var container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await container.StartAsync(cancellationToken).ConfigureAwait(false);

        return new PostgreSqlFixture(container);
    }

    [ExcludeFromCodeCoverage(Justification = "需要 Docker；由 CI 集成测试覆盖，单元测试环境不计入覆盖率。")]
    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

/// <summary>RabbitMQ 夹具。</summary>
public sealed class RabbitMqFixture : IAsyncDisposable
{
    private readonly RabbitMqContainer _container;

    private RabbitMqFixture(RabbitMqContainer container) => _container = container;

    /// <summary>AMQP 连接串。</summary>
    public string ConnectionString => _container.GetConnectionString();

    [ExcludeFromCodeCoverage(Justification = "需要 Docker；由 CI 集成测试覆盖，单元测试环境不计入覆盖率。")]
    public static async Task<RabbitMqFixture> StartAsync(CancellationToken cancellationToken = default)
    {
        var container = new RabbitMqBuilder("rabbitmq:3-management-alpine").Build();
        await container.StartAsync(cancellationToken).ConfigureAwait(false);

        return new RabbitMqFixture(container);
    }

    [ExcludeFromCodeCoverage(Justification = "需要 Docker；由 CI 集成测试覆盖，单元测试环境不计入覆盖率。")]
    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

/// <summary>Kafka（KRaft）夹具。</summary>
public sealed class KafkaFixture : IAsyncDisposable
{
    private readonly KafkaContainer _container;

    private KafkaFixture(KafkaContainer container) => _container = container;

    /// <summary>Bootstrap servers（host:port）。</summary>
    public string BootstrapServers => _container.GetBootstrapAddress();

    [ExcludeFromCodeCoverage(Justification = "需要 Docker；由 CI 集成测试覆盖，单元测试环境不计入覆盖率。")]
    public static async Task<KafkaFixture> StartAsync(CancellationToken cancellationToken = default)
    {
        var container = new KafkaBuilder("confluentinc/cp-kafka:7.6.0").Build();
        await container.StartAsync(cancellationToken).ConfigureAwait(false);

        return new KafkaFixture(container);
    }

    [ExcludeFromCodeCoverage(Justification = "需要 Docker；由 CI 集成测试覆盖，单元测试环境不计入覆盖率。")]
    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

/// <summary>MinIO 夹具（S3 兼容）。</summary>
public sealed class MinioFixture : IAsyncDisposable
{
    private readonly MinioContainer _container;

    private MinioFixture(MinioContainer container) => _container = container;

    /// <summary>HTTP 端点（S3 Endpoint）。</summary>
    public string Endpoint => _container.GetConnectionString();

    [ExcludeFromCodeCoverage(Justification = "需要 Docker；由 CI 集成测试覆盖，单元测试环境不计入覆盖率。")]
    public static async Task<MinioFixture> StartAsync(CancellationToken cancellationToken = default)
    {
        var container = new MinioBuilder("minio/minio:latest").Build();
        await container.StartAsync(cancellationToken).ConfigureAwait(false);

        return new MinioFixture(container);
    }

    [ExcludeFromCodeCoverage(Justification = "需要 Docker；由 CI 集成测试覆盖，单元测试环境不计入覆盖率。")]
    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
