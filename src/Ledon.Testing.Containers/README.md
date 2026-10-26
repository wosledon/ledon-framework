# Ledon.Testing.Containers

集成测试的 Testcontainers 夹具：Redis / PostgreSQL / RabbitMQ / Kafka / MinIO。

```csharp
if (!await LedonContainers.DockerAvailableAsync())
    return;   // 无 Docker 环境跳过

await using var redis = await RedisFixture.StartAsync();
var cache = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
```

| 夹具 | 连接信息 |
| --- | --- |
| `RedisFixture` | `ConnectionString` |
| `PostgreSqlFixture` | `ConnectionString` |
| `RabbitMqFixture` | `ConnectionString`（AMQP） |
| `KafkaFixture` | `BootstrapServers` |
| `MinioFixture` | `Endpoint`（S3 兼容） |

- 基于官方 Testcontainers .NET 模块；
- 容器启动/释放由测试控制，建议配合 `IAsyncLifetime`；
- CI 无 Docker 时用 `DockerAvailableAsync()` 显式跳过，不要伪装通过。

完整文档：<https://github.com/wosledon/ledon-framework>
