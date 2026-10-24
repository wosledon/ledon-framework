# gRPC 服务间通信

内部服务之间的高性能调用用 gRPC（HTTP/2 + 二进制 + 强类型契约）。Ledon 把"服务发现/映射/客户端注册"接到同一套自动发现约定上。

## 服务端：自动发现 + 约定映射

```csharp
builder.Services.AddLedonGrpc();   // AddGrpc + 发现 gRPC 服务实现类
app.MapLedonGrpcServices();        // 逐个映射（幂等）
```

发现判据与 protoc 生成代码的形状对齐：**具体类、非抽象，且基类链上带 `[BindServiceMethod]` 特性**（即 `XxxBase` 的派生类）。业务实现类照常写在模块程序集里：

```csharp
public class GreeterService(GreeterDbContext db) : Greeter.GreeterBase
{
    public override Task<HelloReply> SayHello(HelloRequest request, ServerCallContext context) { /* ... */ }
}
```

- 手动 `MapGrpcService<T>()` 已映射过的服务不会重复映射（多处装配安全）；
- 想精确控制时直接用自己的 `MapGrpcService` 调用，不冲突。

## 客户端：强类型客户端

```csharp
builder.Services.AddLedonGrpcClient<Greeter.GreeterClient>("https://localhost:7001");
```

地址配置驱动；已注册 `Ledon.Resilience` 时自动获得重试/超时/熔断。多服务寻址可接 `Ledon.Aspire` 的服务发现（`https+服务名://`）。

## 健康检查与日志拦截器

```csharp
builder.Services.AddLedonGrpc();   // UseHealthChecks / UseLoggingInterceptor 默认都开
app.MapLedonGrpcServices();        // 一并映射 grpc.health.v1.Health
```

- **标准健康服务**：`grpc.health.v1.Health/Check`（整体状态默认 Serving）——负载均衡/K8s 的 gRPC 探针直接可用，不必自己实现；
- **日志拦截器**：每次调用记录方法、耗时与成败（unary/客户端流/服务端流/双流四种都覆盖）——gRPC 调用不只在链路里可见；
- 关掉任一：`AddLedonGrpc(o => { o.UseHealthChecks = false; o.UseLoggingInterceptor = false; })`。

## 与 HTTP API 的分工

| 场景                        | 选择               |
| --------------------------- | ------------------ |
| 浏览器/第三方开放 API       | `Ledon.Web`（REST）|
| 内部服务间高频、强契约调用  | `Ledon.Grpc`       |
| 异步解耦、跨服务领域事件    | `Ledon.Queues` + 传输 |

## 相关主题

- [Web 开发](~/docs/web.md)——REST 侧能力
- [后台队列与发件箱](~/docs/queues.md)——异步可靠投递
