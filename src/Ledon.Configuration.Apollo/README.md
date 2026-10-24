# Ledon.Configuration.Apollo

Ledon Framework 的 Apollo 配置中心接入：Apollo 作为配置源，支持多命名空间合并、变更热更新。

```csharp
builder.Configuration.AddLedonApollo(o =>
{
    o.ConfigServer = "http://localhost:8080";
    o.AppId = "myapp";                        // Apollo 控制台的应用 ID
    o.Namespaces = ["application", "database"]; // 多命名空间按序合并（后者覆盖同键）
});
```

- **解析**：`application` 命名空间的键直接进配置树；其它命名空间以 `{命名空间}:content` 暴露原始文件内容（properties 之外的 yaml/json 自行解析）；
- **热更新**：默认每 60s 轮询检测变更（releaseKey 比对），变更即触发热重载（`o.PollingInterval` 可调）；
- **与强类型配置配合**：`AddLedonOptions<T>` 照常绑定 Apollo 里的节（含启动校验）；
- **集群/命名空间**：`o.Cluster`（默认 default）与 `o.Namespaces` 可配。

完整文档：<https://github.com/wosledon/ledon-framework>
