# Ledon.Localization

请求文化解析 + 多语言文案：查询串 → Cookie → Accept-Language → 默认，缺文案时键即兜底。

```csharp
builder.Services.AddLedonLocalization(o =>
{
    o.DefaultCulture = "zh-CN";
    o.SupportedCultures.Add("en-US");
});

app.UseLedonRequestLocalization();   // 放在 UseRouting 之前

// 文案：Resources/zh-CN.json、Resources/en-US.json（改文件即时生效）
// { "Order.Created": "订单已创建" }
```

- 支持的文化名单之外的文化被忽略（防乱填 `?culture=xx-XX` 产生怪格式）；
- `Accept-Language` 按 q 值降序匹配，语言相同地区不同时回退（`zh-TW` → `zh-CN`）；
- 请求头选中的文化写回 Cookie 记住选择（`o.RememberChoice = false` 关闭）；
- `CultureInfo.CurrentCulture` 自动设置，DateTime/数字格式随文化变化；
- 文案源可换：`AddLedonLocalizationStore<InMemoryLocalizationStore>()` 或数据库实现；
- 坏 JSON 不当异常冒到请求管道：视为无文案（键兜底）。

完整文档：<https://github.com/wosledon/ledon-framework>
