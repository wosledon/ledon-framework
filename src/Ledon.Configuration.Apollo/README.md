# Ledon.Configuration.Apollo

Ledon Framework 的 Apollo 配置中心接入：Apollo 作为配置源，支持多命名空间合并、`notifications/v2` 长轮询热更新。

```csharp
builder.Configuration.AddLedonApollo(o =>
{
    o.ConfigServer = "http://localhost:8080";
    o.AppId = "myapp";                        // Apollo 控制台的应用 ID
    o.Namespaces = ["application", "database"]; // 多命名空间按序合并（后者覆盖同键）
});
```

- **解析**：`application` 命名空间的键直接进配置树；其它命名空间以 `{命名空间}:{键}` 暴露；
- **长轮询**：默认走 `/notifications/v2`（挂起 60s），变更秒级热重载；服务端不支持时自动回退 `PollingInterval` 定时轮询；
- **与强类型配置配合**：`AddLedonOptions<T>` 照常绑定 Apollo 里的节（含启动校验）；
- **集群/命名空间**：`o.Cluster`（默认 default）与 `o.Namespaces` 可配。

完整文档：<https://github.com/wosledon/ledon-framework>