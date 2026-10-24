# Ledon.Configuration

Ledon Framework 的强类型配置约定：绑定 + DataAnnotations/IValidatableObject 启动校验。

```csharp
builder.Services.AddLedonOptions<SmtpOptions>(builder.Configuration);
// appsettings.json: { "Smtp": { "Host": "smtp.example.com", "Port": 587 } }
```

- **节名 = 类型名去掉 `Options` 后缀**（`SmtpOptions` → 节 `Smtp`）；自定义节名显式传 `sectionName`；
- 自动绑定 + `DataAnnotations` 校验；
- `ValidateOnStart`：**启动即失败**——配错了在启动时抛 `OptionsValidationException`，不拖到运行时；
- 实现 `IValidatableObject` 的选项（跨字段校验）同样在启动时校验。

```csharp
public class SmtpOptions
{
    [Required] public string Host { get; set; } = string.Empty;

    [Range(1, 65535)] public int Port { get; set; } = 25;
}
```

完整文档：<https://github.com/wosledon/ledon-framework>
