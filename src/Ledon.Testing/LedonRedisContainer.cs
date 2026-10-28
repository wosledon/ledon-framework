using System.Diagnostics.CodeAnalysis;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;

namespace Ledon.Testing;

/// <summary>
/// 测试用 Redis 容器（Testcontainers）：真实 Redis 跑集成测试，而不是模拟。
/// 需要本机 Docker（CI 用 ubuntu-latest 自带）。
/// <code>
/// await using var redis = new LedonRedisContainer();
/// var connectionString = await redis.StartAsync();
/// </code>
/// </summary>
public sealed class LedonRedisContainer : IAsyncDisposable
{
    private readonly ContainerBuilder _builder = new ContainerBuilder("redis:7-alpine")
        .WithPortBinding(6379, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379));

    private DotNet.Testcontainers.Containers.IContainer? _container;

    /// <summary>启动容器并返回连接串（host:port）。</summary>
    [ExcludeFromCodeCoverage(Justification = "需要 Docker；由 CI 集成测试覆盖，单元测试环境不计入覆盖率。")]
    public async Task<string> StartAsync(CancellationToken cancellationToken = default)
    {
        _container = _builder.Build();
        await _container.StartAsync(cancellationToken).ConfigureAwait(false);

        return ConnectionString = $"{_container.Hostname}:{_container.GetMappedPublicPort(6379)}";
    }

    /// <summary>连接串（host:port），<see cref="StartAsync"/> 之后可用。</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    [ExcludeFromCodeCoverage(Justification = "需要 Docker；由 CI 集成测试覆盖，单元测试环境不计入覆盖率。")]
    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync().ConfigureAwait(false);
    }
}
