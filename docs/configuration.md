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

## 密钥

签名密钥、连接字符串等机密从环境变量/密钥管理注入（`OTEL_EXPORTER_OTLP_ENDPOINT` 等标准变量自动生效），切勿提交进 appsettings.json。
