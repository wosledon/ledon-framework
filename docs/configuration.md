# 强类型配置

```csharp
builder.Services.AddLedonOptions<SmtpOptions>(builder.Configuration);
// appsettings.json: { "Smtp": { "Host": "smtp.example.com", "Port": 587 } }
```

## 约定

- **节名 = 类型名去掉 `Options` 后缀**（`SmtpOptions` → 节 `Smtp`；`JwtOptions` → 节 `Jwt`）；需要自定义节名时显式传 `sectionName`；
- 自动绑定 + `DataAnnotations` 校验；
- `ValidateOnStart`：**启动即失败**——配错了在启动时抛 `OptionsValidationException`，不拖到运行时首次用到才炸；
- 实现 `IValidatableObject` 的选项同样在启动时校验（跨字段校验）。

```csharp
public class SmtpOptions
{
    [Required] public string Host { get; set; } = string.Empty;

    [Range(1, 65535)] public int Port { get; set; } = 25;
}
```

读取与标准 options 模式一致：注入 `IOptions<SmtpOptions>` / `IOptionsSnapshot<>` / `IOptionsMonitor<>`。

## 与模块配置的关系
`ModuleContext.Configuration` 暴露宿主配置（任意节、任意键），适合模块读取**共享配置**；`AddLedonOptions<T>` 适合模块**自己的强类型选项**（带校验契约）。两者可混用。

## 配置中心（Nacos）

远程配置 + 热更新。强类型配置（`AddLedonOptions<T>`）配 `IOptionsMonitor<T>` 消费，
配置中心变更后自动拿到新值（已对该链路做集成测试）：

```csharp
// Program.cs
builder.Configuration.AddLedonNacos(o => { o.ServerAddresses = ["http://nacos:8848"]; o.DataIds = ["app"]; });
builder.Services.AddLedonOptions<SmtpOptions>(builder.Configuration);

// 业务代码：改配置中心 → CurrentValue 自动变
public class Mailer(IOptionsMonitor<SmtpOptions> options)
{
    public string Host => options.CurrentValue.Host;
}
```

- 轮询检测变更（默认 30s）→ `IConfiguration` 重载 → `IOptionsMonitor` 联动；
- Apollo（`AddLedonApollo`）同一套语义；多命名空间/多 DataId 合并规则见各包文档；
- 启动时拉不到配置直接失败（不带病启动）；轮询失败不抛，旧配置继续生效。
要把配置托管到 Nacos（多环境集中管理 + 变更热更新）时装 `Ledon.Configuration.Nacos`：

```csharp
builder.Configuration.AddLedonNacos(o =>
{
    o.ServerAddresses = ["http://localhost:8848"];   // 多地址按序故障转移
    o.DataIds = ["myapp", "myapp-database"];         // 多配置按序合并
});
```

- properties（`key=value`）与 JSON（嵌套拍平为 `a:b:c`）均可解析；
- 默认 30s 轮询检测变更并热重载；`AddLedonOptions<T>` 照常绑定（含启动校验）；
- 开启鉴权时配 `o.UserName`/`o.Password`（401 自动重登）。

Apollo 用户装 `Ledon.Configuration.Apollo`（用法一致）：

```csharp
builder.Configuration.AddLedonApollo(o =>
{
    o.ConfigServer = "http://localhost:8080";
    o.AppId = "myapp";
    o.Namespaces = ["application", "database"];   // 多命名空间合并
});
```

## 密钥

签名密钥、连接字符串等机密从环境变量/密钥管理注入（`OTEL_EXPORTER_OTLP_ENDPOINT` 等标准变量自动生效），切勿提交进 appsettings.json。
