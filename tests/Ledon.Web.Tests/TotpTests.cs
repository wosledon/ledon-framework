using Ledon.Security.Totp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ledon.Web.Tests;

/// <summary>TOTP：RFC 6238 向量、窗口校验、Base32 与恢复码。</summary>
public class TotpTests
{
    private const string RfcSecret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    [Theory]
    [InlineData(59, "94287082")]
    [InlineData(1111111109, "07081804")]
    [InlineData(1111111111, "14050471")]
    public void Rfc6238_vectors_match(long unixTime, string expected)
    {
        var service = new TotpService(new TotpOptions { Digits = 8, VerificationWindow = 0 });

        Assert.Equal(expected, service.GetCode(RfcSecret, DateTimeOffset.FromUnixTimeSeconds(unixTime)));
        Assert.True(service.Validate(RfcSecret, expected, DateTimeOffset.FromUnixTimeSeconds(unixTime)));
    }

    [Fact]
    public void Validate_accepts_adjacent_window_and_rejects_wrong_code()
    {
        var service = new TotpService(new TotpOptions { VerificationWindow = 1 });
        var secret = service.GenerateSecret();
        var now = DateTimeOffset.UtcNow;
        var previous = service.GetCode(secret, now.AddSeconds(-30));

        Assert.True(service.Validate(secret, previous, now));
        Assert.False(service.Validate(secret, "000000", now, window: 0));
        Assert.False(service.Validate(secret, "abcdef", now));
        Assert.False(service.Validate(secret, service.GetCode(secret, now.AddSeconds(-300)), now, window: 0));
    }

    [Fact]
    public void Base32_roundtrip_and_otpauth_uri()
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes("12345678901234567890");
        var encoded = Base32.Encode(bytes);

        Assert.Equal(RfcSecret, encoded);
        Assert.Equal(bytes, Base32.Decode(encoded));
        Assert.Equal(bytes, Base32.Decode(encoded.ToLowerInvariant()));

        var service = new TotpService(new TotpOptions { Issuer = "Ledon Cloud" });
        var uri = service.BuildUri("alice@example.com", encoded);

        Assert.StartsWith("otpauth://totp/", uri);
        Assert.Contains("secret=" + encoded, uri);
        Assert.Contains("issuer=Ledon%20Cloud", uri);
    }

    [Fact]
    public void Recovery_codes_are_hashed_and_single_match()
    {
        var service = new RecoveryCodeService(new TotpOptions { RecoveryCodeCount = 3, RecoveryCodeLength = 10 });
        var codes = service.Generate();

        Assert.Equal(3, codes.Count);
        Assert.All(codes, code => Assert.Equal(10, code.Code.Length));
        Assert.All(codes, code => Assert.Equal(64, code.Hash.Length));
        Assert.Equal(codes.Count, codes.Select(code => code.Hash).Distinct().Count());

        var hashes = codes.Select(code => code.Hash).ToArray();

        Assert.True(service.TryConsume(codes[1].Code.ToLowerInvariant(), hashes, out var matched));
        Assert.Equal(codes[1].Hash, matched);
        Assert.False(service.TryConsume("WRONGCODE1", hashes, out _));
    }

    [Fact]
    public void Registration_resolves_services()
    {
        var services = new ServiceCollection();
        services.AddLedonTotp(o => o.Issuer = "Test");

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<ITotpService>());
        Assert.NotNull(provider.GetRequiredService<IRecoveryCodeService>());
    }
}
