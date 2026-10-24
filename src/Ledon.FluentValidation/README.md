# Ledon.FluentValidation

Ledon Framework 的 FluentValidation 集成：验证器自动发现，失败统一 400 ValidationProblemDetails。

```csharp
builder.Services.AddLedonFluentValidation();
```

```csharp
public class EchoRequestValidator : AbstractValidator<EchoRequest>
{
    public EchoRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(50);
    }
}
```

- 被扫描程序集里的 `AbstractValidator<T>` 实现**自动发现并注册**（沿用框架允许列表：引用框架 = 选择加入），手动注册优先；
- 与 `[ApiController]` 的模型验证同一出口——失败即 400 `ValidationProblemDetails`（含 `errors` 字典），与 DataAnnotations 规则一致；
- 与 DataAnnotations 可混用（两种规则的失败都会汇入 ModelState）。

最小 API 场景用 FluentValidation 官方的 `AddFluentValidationAutoValidation()` 标准用法即可（本包主要补足控制器场景的自动发现）。

完整文档：<https://github.com/wosledon/ledon-framework>
