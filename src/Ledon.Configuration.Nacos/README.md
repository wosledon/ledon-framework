# Ledon.Configuration.Nacos

Ledon Framework 的 Nacos 配置中心接入：Nacos 作为配置源，支持多 DataId 合并、properties/JSON 解析、变更热更新。

```csharp
builder.Configuration.AddLedonNacos(o =>
{
    o.ServerAddresses = ["http://localhost:8848"];   // 多地址按序故障转移
    o.DataIds = ["myapp", "myapp-database"];         // 多配置按序合并（后者覆盖同键）
});
```

- **解析**：properties（`key=value`、`#` 注释）与 JSON（嵌套拍平为 `a:b:c` 键）均可；
- **热更新**：默认每 30s 轮询检测变更，变更即触发热重载（`o.PollingInterval` 可调）；
- **与强类型配置配合**：`AddLedonOptions<T>` 照常绑定 Nacos 里的节（含启动校验）；
- **鉴权**：控制台开启鉴权时配 `o.UserName` / `o.Password`（自动登录换 accessToken，401 自动重登）；
- **命名空间**：`o.Namespace`（tenant）与 `o.Group` 可配。

完整文档：<https://github.com/wosledon/ledon-framework>
