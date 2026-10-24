# Ledon.Testing

Ledon Framework 的测试基建：一行搭建 TestServer 宿主 + 模块生命周期断言。

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

`LedonWebTestHost` 默认走标准 `UseLedonWeb()` 管线（可覆盖），模块系统、自动发现、生命周期与生产完全一致——测的是真实启动路径。`WithScopeAsync` 在独立作用域中解析 Scoped 服务（如 DbContext）。

`ModuleAssertions` 做模块生命周期断言（抛普通异常，不绑定具体测试框架，xunit/NUnit/MSTest 通用）：

```csharp
host.Services.GetRequiredService<IModuleManager>()
    .ShouldBeInitializedInOrder("DataModule", "UserModule");
```

真实基础设施（Testcontainers，需 Docker）：`new LedonRedisContainer()` / `new LedonPostgresContainer()`，`StartAsync()` 返回连接串。

完整文档：<https://github.com/wosledon/ledon-framework>
