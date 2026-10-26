using System.Security.Cryptography;
using System.Text;

namespace Ledon.Security.Totp;

/// <summary>TOTP 参数（RFC 6238 默认值）。</summary>
public sealed class TotpOptions
{
    /// <summary>发行方（otpauth URI），默认 Ledon。</summary>
    public string Issuer { get; set; } = "Ledon";

    /// <summary>验证码位数，默认 6（支持 6-8）。</summary>
    public int Digits { get; set; } = 6;

    /// <summary>时间步长，默认 30 秒。</summary>
    public TimeSpan Period { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>密钥字节数，默认 20（SHA-1 推荐）。</summary>
    public int SecretSize { get; set; } = 20;

    /// <summary>HMAC 算法，默认 SHA-1（兼容绝大多数验证器）。</summary>
    public TotpAlgorithm Algorithm { get; set; } = TotpAlgorithm.Sha1;

    /// <summary>校验时允许前后偏移的时间步数，默认 1（±30 秒）。</summary>
    public int VerificationWindow { get; set; } = 1;

    /// <summary>恢复码数量，默认 10。</summary>
    public int RecoveryCodeCount { get; set; } = 10;

    /// <summary>恢复码字符数（Base32），默认 10。</summary>
    public int RecoveryCodeLength { get; set; } = 10;
}

/// <summary>TOTP HMAC 算法。</summary>
public enum TotpAlgorithm
{
    Sha1,
    Sha256,
    Sha512,
}

/// <summary>TOTP 服务（RFC 6238 / RFC 4226 动态截断）。</summary>
public interface ITotpService
{
    /// <summary>生成 Base32 密钥。</summary>
    string GenerateSecret();

    /// <summary>计算指定时间的验证码。</summary>
    string GetCode(string secret, DateTimeOffset? timestamp = null);

    /// <summary>校验验证码；<paramref name="window"/> 为空时用 <see cref="TotpOptions.VerificationWindow"/>。</summary>
    bool Validate(string secret, string code, DateTimeOffset? timestamp = null, int? window = null);

    /// <summary>生成 otpauth:// URI（二维码内容）。</summary>
    string BuildUri(string accountName, string secret);
}

/// <summary>默认 TOTP 服务。</summary>
public sealed class TotpService(TotpOptions options) : ITotpService
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public string GenerateSecret()
    {
        if (options.SecretSize < 10)
            throw new InvalidOperationException("TotpOptions.SecretSize 至少为 10 字节");

        return Base32.Encode(RandomNumberGenerator.GetBytes(options.SecretSize));
    }

    public string GetCode(string secret, DateTimeOffset? timestamp = null)
    {
        ValidateOptions();

        var key = Base32.Decode(secret);
        var counter = (long)((timestamp ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / (long)options.Period.TotalSeconds);

        Span<byte> counterBytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counterBytes, counter);

        using HMAC hmac = options.Algorithm switch
        {
            TotpAlgorithm.Sha1 => new HMACSHA1(key),
            TotpAlgorithm.Sha256 => new HMACSHA256(key),
            TotpAlgorithm.Sha512 => new HMACSHA512(key),
            _ => throw new InvalidOperationException($"未知 TOTP 算法：{options.Algorithm}"),
        };

        Span<byte> hash = stackalloc byte[hmac.HashSize / 8];
        hmac.TryComputeHash(counterBytes, hash, out _);

        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
            | ((hash[offset + 1] & 0xFF) << 16)
            | ((hash[offset + 2] & 0xFF) << 8)
            | (hash[offset + 3] & 0xFF);

        var code = binary % (int)Math.Pow(10, options.Digits);
        return code.ToString($"D{options.Digits}", System.Globalization.CultureInfo.InvariantCulture);
    }

    public bool Validate(string secret, string code, DateTimeOffset? timestamp = null, int? window = null)
    {
        if (string.IsNullOrWhiteSpace(code))
            return false;

        var expectedLength = options.Digits;
        var normalized = code.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);

        if (normalized.Length != expectedLength || !normalized.All(char.IsAsciiDigit))
            return false;

        var tolerance = Math.Max(0, window ?? options.VerificationWindow);
        var now = timestamp ?? DateTimeOffset.UtcNow;

        foreach (var offset in Enumerable.Range(-tolerance, tolerance * 2 + 1))
        {
            var candidate = GetCode(secret, now.AddSeconds(offset * options.Period.TotalSeconds));

            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(candidate),
                    Encoding.ASCII.GetBytes(normalized)))
            {
                return true;
            }
        }

        return false;
    }

    public string BuildUri(string accountName, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        var algorithm = options.Algorithm switch
        {
            TotpAlgorithm.Sha256 => "SHA256",
            TotpAlgorithm.Sha512 => "SHA512",
            _ => "SHA1",
        };

        var label = Uri.EscapeDataString($"{options.Issuer}:{accountName}");
        var query = string.Join('&',
            $"secret={Uri.EscapeDataString(secret)}",
            $"issuer={Uri.EscapeDataString(options.Issuer)}",
            $"algorithm={algorithm}",
            $"digits={options.Digits}",
            $"period={(int)options.Period.TotalSeconds}");

        return $"otpauth://totp/{label}?{query}";
    }

    private void ValidateOptions()
    {
        if (options.Digits is < 6 or > 8)
            throw new InvalidOperationException("TotpOptions.Digits 必须为 6-8");

        if (options.Period <= TimeSpan.Zero)
            throw new InvalidOperationException("TotpOptions.Period 必须大于 0");

        if (options.VerificationWindow < 0)
            throw new InvalidOperationException("TotpOptions.VerificationWindow 不能为负数");
    }
}

/// <summary>一次性恢复码（SHA-256 哈希存储）。</summary>
public interface IRecoveryCodeService
{
    /// <summary>生成恢复码及哈希（明文只展示一次）。</summary>
    IReadOnlyList<RecoveryCode> Generate();

    /// <summary>校验恢复码并返回命中的哈希（用于从存储中删除）。</summary>
    bool TryConsume(string code, IEnumerable<string> hashes, out string? matchedHash);
}

/// <summary>恢复码明文与哈希。</summary>
public sealed record RecoveryCode(string Code, string Hash);

/// <summary>默认恢复码服务。</summary>
public sealed class RecoveryCodeService(TotpOptions options) : IRecoveryCodeService
{
    public IReadOnlyList<RecoveryCode> Generate()
    {
        var count = Math.Max(1, options.RecoveryCodeCount);
        var length = Math.Clamp(options.RecoveryCodeLength, 8, 32);
        var codes = new List<RecoveryCode>(count);

        for (var i = 0; i < count; i++)
        {
            var code = Base32
                .Encode(RandomNumberGenerator.GetBytes((length * 5 + 7) / 8))[..length];

            codes.Add(new RecoveryCode(code, Hash(code)));
        }

        return codes;
    }

    public bool TryConsume(string code, IEnumerable<string> hashes, out string? matchedHash)
    {
        matchedHash = null;

        if (string.IsNullOrWhiteSpace(code))
            return false;

        var hash = Hash(code);

        foreach (var candidate in hashes)
        {
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(hash),
                    Encoding.ASCII.GetBytes(candidate)))
            {
                matchedHash = candidate;
                return true;
            }
        }

        return false;
    }

    private static string Hash(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(code)))).ToLowerInvariant();

    private static string Normalize(string code) =>
        code.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
}

/// <summary>RFC 4648 Base32（无填充）。</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return string.Empty;

        var output = new StringBuilder((data.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;

        foreach (var value in data)
        {
            buffer = (buffer << 8) | value;
            bits += 8;

            while (bits >= 5)
            {
                output.Append(Alphabet[(buffer >> (bits - 5)) & 0x1F]);
                bits -= 5;
            }
        }

        if (bits > 0)
            output.Append(Alphabet[(buffer << (5 - bits)) & 0x1F]);

        return output.ToString();
    }

    public static byte[] Decode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var normalized = value
            .Trim()
            .TrimEnd('=')
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();

        if (normalized.Length == 0)
            return [];

        if (normalized.Length % 8 == 1)
            throw new FormatException("Base32 字符串长度非法");

        var output = new List<byte>(normalized.Length * 5 / 8);
        var buffer = 0;
        var bits = 0;

        foreach (var character in normalized)
        {
            var index = Alphabet.IndexOf(character, StringComparison.Ordinal);

            if (index < 0)
                throw new FormatException($"Base32 字符非法：{character}");

            buffer = (buffer << 5) | index;
            bits += 5;

            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. output];
    }
}
