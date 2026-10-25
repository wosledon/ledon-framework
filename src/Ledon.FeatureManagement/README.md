# Ledon.FeatureManagement

功能开关与灰度：未登记的功能默认关（fail-closed），按开关名/租户/用户/百分比定向，配置中心热更新。

```csharp
builder.Services.AddLedonFeatureManagement();

// 配置（或 Nacos/Apollo 远程配置，同样生效）
// "Features": {
//   "new-checkout": true,
//   "beta-report":  { "Percentage": 20 },
//   "vip-only":     { "Tenants": [ "t-001", "t-002" ] }
// }
```

业务代码只问"开没开"：

```csharp
public class CheckoutService(IFeatureManager features)
{
    public async Task CheckoutAsync(Cart cart, CancellationToken ct = default)
    {
        if (await features.IsEnabledAsync("new-checkout", ct: ct))
            await NewCheckoutAsync(cart, ct);
        else
            await LegacyCheckoutAsync(cart, ct);
    }
}
```

端点级开关（未启用返回 404，不暴露功能存在性）：

```csharp
app.MapGet("/beta/report", () => Results.Ok(...))
   .AddEndpointFilter(new FeatureGateFilter("beta-report"));
```

- 判定顺序：显式开关 → 租户白名单 → 用户白名单 → 百分比灰度；
- 百分比分桶稳定：同一主体在比例不变时判定不变（不会一会儿开一会儿关）；
- 运行时 `SetEnabled` 可即时改开关（管理端用）；
- 未登记的开关返回默认值（默认 `false`）。

完整文档：<https://github.com/wosledon/ledon-framework>
