# 测试基建

```csharp
using Ledon.Testing;

public class HelloTests
{
    [Fact]
    public async Task Hello_endpoint_returns_world()
    {
        await using var host = await LedonWebTestHost.StartAsync(
            builder => builder.AddLedonWeb());

        var body = await host.Client.GetStringAsync("/hello");
        Assert.Equal("world", body);
    }
}
```

`LedonWebTestHost` 一行搭建 TestServer 宿主：默认走标准 `UseLedonWeb()` 管线（可覆盖），模块系统、自动发现、生命周期与生产完全一致——测的是真实启动路径，不是模拟。

## 解析 Scoped 服务

```csharp
var count = await host.WithScopeAsync(async sp =>
    await sp.GetRequiredService<TodoDbContext>().Todos.CountAsync());
```

## 模块生命周期断言

```csharp
host.Services.GetRequiredService<IModuleManager>()
    .ShouldBeInitializedInOrder("DataModule", "UserModule");
```

`ModuleAssertions` 抛普通异常而非绑定具体测试框架（xunit / NUnit / MSTest 通用）。

## 实践建议

- **集成测试优先**：框架的价值在自动接线，集成测试才能覆盖发现/注入/生命周期全链路；
- 每用例独立 SQLite 文件（见 `tests/` 夹具的做法），避免文件锁与状态串扰；
- 端到端验证规范化行为（ProblemDetails、校验 400、关联 ID）——这些是框架契约，回归代价高。

## 真实基础设施（Testcontainers）

需要真实 Redis/PostgreSQL（而不是 SQLite 近似）时用容器夹具——本机或 CI（ubuntu-latest 自带 Docker）有 Docker 即可：

```csharp
await using var redis = new LedonRedisContainer();
var connectionString = await redis.StartAsync();   // host:port

await using var postgres = new LedonPostgresContainer();
var npgsql = await postgres.StartAsync();          // Host=...;Port=...;Username=ledon...
```

CI 上跑这些用例需要 Docker 环境；无 Docker 时测试应自行跳过（探测 `docker info` 退出码，参考 `tests/` 里的 `DockerAvailableAsync` 做法）。
