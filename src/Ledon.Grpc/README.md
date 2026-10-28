# Ledon.Grpc

gRPC 服务间通信：**服务自动发现 + 约定映射 + 强类型客户端 + 健康检查/日志/异常拦截器**。
与框架其它能力一样，引用即选择加入，不需要逐个 `MapGrpcService`。

```csharp
// 服务端：发现并映射业务程序集里的 gRPC 服务实现
builder.Services.AddLedonGrpc();
app.MapLedonGrpcServices();

// 客户端：强类型客户端，地址配置驱动
builder.Services.AddLedonGrpcClient<Greeter.GreeterClient>("https://localhost:7001");
```

- 服务发现判据与 protoc 生成代码对齐：具体类、非抽象，基类链上带 `[BindServiceMethod]`；
- 默认注册 `grpc.health.v1.Health` 标准健康服务，K8s/LB 探针可直接使用；
- 默认日志拦截器记录方法、耗时与成败；默认异常拦截器把业务异常映射为合适的 gRPC 状态码：
  `ArgumentException → InvalidArgument`、`KeyNotFoundException → NotFound`、`UnauthorizedAccessException → PermissionDenied`；
- 自定义映射实现 `IGrpcExceptionMapper` 并注册进容器，优先于默认表；
- 客户端未传 deadline 时使用 `LedonGrpcOptions.DefaultDeadline`（默认 30 秒）兜底；
- 多服务寻址可接 `Ledon.Aspire` 的服务发现（`https+服务名://`）。

> `Ledon.Resilience` 的 HttpClient 策略不会自动套到 gRPC 调用上（gRPC 抛 `RpcException`）；
> 需要重试/熔断时按 `RpcException.Status.StatusCode` 自建策略或配置 `GrpcClientFactoryOptions`。

完整文档：<https://github.com/wosledon/ledon-framework>
