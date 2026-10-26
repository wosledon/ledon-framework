# Ledon.Security.Totp

RFC 6238 TOTP（Google Authenticator / 1Password / Microsoft Authenticator 兼容）与一次性恢复码。

```csharp
builder.Services.AddLedonTotp(o =>
{
    o.Issuer = "MyApp";
    o.Digits = 6;
    o.VerificationWindow = 1; // 允许前后 1 个 30 秒窗口
});
```

```csharp
var secret = totp.GenerateSecret();                     // Base32，展示给用户录入
var uri = totp.BuildUri("alice@example.com", secret);   // 生成二维码
var valid = totp.Validate(secret, userCode);

var recoveryCodes = recoveryCodesService.Generate();    // 明文只返回一次
var ok = recoveryCodesService.TryConsume(input, storedHashes, out var matchedHash);
```

- 默认 HMAC-SHA1 / 6 位 / 30 秒，支持 SHA-256、SHA-512；
- 校验使用固定时间比较，支持前后窗口容忍时钟漂移；
- 恢复码以 SHA-256 哈希存储，命中后由调用方从存储删除（一次性）；
- 密钥与恢复码请加密存储（可配合 `Ledon.Data.Encryption` / `Ledon.Settings`）。

完整文档：<https://github.com/wosledon/ledon-framework>
