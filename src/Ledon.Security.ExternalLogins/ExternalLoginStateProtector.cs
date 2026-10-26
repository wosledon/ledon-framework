using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Ledon.Security.ExternalLogins;

/// <summary>用 Data Protection 保护 OAuth state（防篡改 + 过期）。</summary>
public sealed class ExternalLoginStateProtector
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDataProtector _protector;
    private readonly ExternalLoginOptions _options;

    public ExternalLoginStateProtector(
        IDataProtectionProvider protectionProvider,
        ExternalLoginOptions options)
    {
        _options = options;
        _protector = protectionProvider.CreateProtector(options.StateProtectionPurpose);
    }

    /// <summary>生成受保护的 state。</summary>
    public string Protect(string provider, string? returnUrl = null)
    {
        var state = new ExternalLoginState(
            provider,
            returnUrl,
            DateTimeOffset.UtcNow.Add(_options.StateLifetime));

        return _protector.Protect(JsonSerializer.Serialize(state, JsonOptions));
    }

    /// <summary>解保护并校验过期；失败抛 <see cref="InvalidOperationException"/>。</summary>
    public ExternalLoginState Unprotect(string state)
    {
        if (string.IsNullOrWhiteSpace(state))
            throw new InvalidOperationException("缺少 state");

        ExternalLoginState? payload;

        try
        {
            payload = JsonSerializer.Deserialize<ExternalLoginState>(_protector.Unprotect(state), JsonOptions);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("state 无效或已被篡改", ex);
        }

        if (payload is null || payload.IsExpired)
            throw new InvalidOperationException("state 已过期");

        return payload;
    }
}
