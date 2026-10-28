using System.Diagnostics.CodeAnalysis;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;

namespace Ledon.Testing;

/// <summary>
/// 测试用 PostgreSQL 容器（Testcontainers）：真实 PostgreSQL 跑数据层集成测试
/// （迁移、查询、发件箱），而不是 SQLite 近似。需要本机 Docker（CI 用 ubuntu-latest 自带）。
/// <code>
/// await using var postgres = new LedonPostgresContainer();
/// var connectionString = await postgres.StartAsync();
/// options.UseNpgsql(connectionString);
/// </code>
/// </summary>
public sealed class LedonPostgresContainer : IAsyncDisposable
{
    private const int Port = 5432;

    private readonly ContainerBuilder _builder = new ContainerBuilder("postgres:16-alpine")
        .WithPortBinding(Port, true)
        .WithEnvironment("POSTGRES_USER", "ledon")
        .WithEnvironment("POSTGRES_PASSWORD", "ledon")
        .WithEnvironment("POSTGRES_DB", "ledon")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(Port));

    private DotNet.Testcontainers.Containers.IContainer? _container;

    /// <summary>启动容器并返回连接串（host:port, user=ledon, password=ledon, db=ledon）。</summary>
    [ExcludeFromCodeCoverage(Justification = "需要 Docker；由 CI 集成测试覆盖，单元测试环境不计入覆盖率。")]
    public async Task<string> StartAsync(CancellationToken cancellationToken = default)
    {
        _container = _builder.Build();
        await _container.StartAsync(cancellationToken).ConfigureAwait(false);

        var host = _container.Hostname;
        var port = _container.GetMappedPublicPort(Port);
        return ConnectionString = $"Host={host};Port={port};Database=ledon;Username=ledon;Password=ledon";
    }

    /// <summary>连接串，<see cref="StartAsync"/> 之后可用。</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    [ExcludeFromCodeCoverage(Justification = "需要 Docker；由 CI 集成测试覆盖，单元测试环境不计入覆盖率。")]
    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync().ConfigureAwait(false);
    }
}
