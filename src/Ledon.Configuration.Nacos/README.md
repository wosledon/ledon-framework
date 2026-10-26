# Ledon.Configuration.Nacos

Ledon Framework 的 Nacos 配置中心接入：Nacos 作为配置源，支持多 DataId 合并、properties/JSON 解析、原生长轮询热更新。

```csharp
builder.Configuration.AddLedonNacos(o =>
{
    o.ServerAddresses = ["http://localhost:8848"];   // 多地址按序故障转移
    o.DataIds = ["myapp", "myapp-database"];         // 多配置按序合并（后者覆盖同键）
});
```

- **解析**：properties（`key=value`、`#` 注释）与 JSON（嵌套拍平为 `a:b:c` 键）均可；
- **长轮询**：默认走 `/v1/cs/configs/listener`（挂起 30s），变更秒级热重载；服务端不支持时自动回退 `PollingInterval` 定时轮询；
- **与强类型配置配合**：`AddLedonOptions<T>` 照常绑定 Nacos 里的节（含启动校验）；
- **鉴权**：`o.UserName` / `o.Password`（401 自动重登），或直接配 `o.AccessToken` 跳过登录；
- **命名空间**：`o.Namespace`（tenant）与 `o.Group` 可配。

完整文档：<https://github.com/wosledon/ledon-framework>