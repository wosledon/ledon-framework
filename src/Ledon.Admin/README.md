# Ledon.Admin

统一运维端点：把发件箱、后台队列、死信、工作流、定时任务的状态聚到一起（只读 + 死信重放）。

```csharp
builder.Services.AddLedonAdmin();   // 前缀 /admin/ops
```

| 端点 | 回答什么 |
| --- | --- |
| `GET /admin/ops/summary` | 一页总览：待投递/死信数、队列累计指标、运行中的工作流、定时任务与近期失败 |
| `GET /admin/ops/outbox/dead-letters` | 需要人工介入的搁置消息清单 |
| `POST /admin/ops/outbox/dead-letters/{id}/replay` | 重放单条死信（清零重试计数，回到投递队列） |
| `GET /admin/ops/scheduler` | 定时任务清单 + 最近执行历史 |

- 各能力包可选：没接入发件箱/工作流/调度时，快照里对应项为 `null`，端点不报错；
- 队列指标（投递成功/重试/搁置/处理失败/去重跳过）经 `MeterListener` 读取，是 Prometheus 之外的第二条路径；
- 只有死信重放一个写操作，其余全部只读——运维面板不会误改业务数据；
- 生产环境应加认证授权（与业务端点同一套策略）。

完整文档：<https://github.com/wosledon/ledon-framework>
