# Ledon.Workflow

多步骤业务流程编排：**步骤自动发现、推进即持久化、失败重试 + Saga 补偿、重启恢复**。
适合订单履约、对账、KYC 审核、数据迁移管线等"必须按顺序执行、失败要撤销"的场景。

```csharp
builder.Services.AddLedonWorkflow();   // 步骤/定义自动发现 + 恢复服务

public class OrderWorkflow : IWorkflowDefinition
{
    public string Name => "order";
    public string InitialStep => "reserve";
    public IReadOnlyDictionary<string, string[]> Transitions => new()
    {
        ["reserve"] = ["charge"],
        ["charge"] = ["ship", "cancel"],   // 多出口：由步骤写 Data["next"] 选择
    };
}

var engine = provider.GetRequiredService<WorkflowEngine>();
var instance = await engine.CreateAsync("order", new Dictionary<string, object?> { ["orderId"] = 42 });
var done = await engine.RunAsync(instance.Id, ct);
```

- 实现 `IWorkflowStep` 即被自动发现；实现 `ICompensableStep` 获得失败补偿；
- 每次推进后保存实例（含数据与已完成步骤），进程崩溃不丢进度；
- 托管服务启动即恢复 `Running` 实例，之后每 30 秒轮询；
- 单步骤失败重试至 `WorkflowOptions.MaxRetriesPerStep`（默认 3），重试耗尽后逆序补偿；
- 补偿失败只记录日志、不阻断其余补偿；最终抛 `WorkflowExecutionException` 并带完整现场；
- 持久化：DbContext 继承 `WorkflowDbContext` 获得实例表；也可用 `InMemoryWorkflowStore` 做测试。

完整文档：<https://github.com/wosledon/ledon-framework>
