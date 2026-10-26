using Ledon.Security.Totp;
using Xunit;

namespace Ledon.Web.Tests;

/// <summary>TOTP 边界：非法 Base32 与恢复码规范化。</summary>
public class TotpEdgeTests
{
    [Fact]
    public void Invalid_base32_secret_throws_format_exception()
    {
        var service = new TotpService(new TotpOptions());

        Assert.Throws<FormatException>(() => service.GetCode("not-base32!", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Recovery_code_accepts_hyphen_and_lowercase()
    {
        var service = new RecoveryCodeService(new TotpOptions { RecoveryCodeCount = 1, RecoveryCodeLength = 10 });
        var code = service.Generate()[0];
        var formatted = code.Code.ToLowerInvariant();
        formatted = formatted[..5] + "-" + formatted[5..];

        Assert.True(service.TryConsume(formatted, [code.Hash], out var matched));
        Assert.Equal(code.Hash, matched);
    }
}
