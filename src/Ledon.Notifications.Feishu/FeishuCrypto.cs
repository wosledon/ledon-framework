using System.Security.Cryptography;
using System.Text;

namespace Ledon.Notifications.Feishu;

/// <summary>
/// 飞书签名与加解密（自定义机器人签名 HMAC-SHA256；事件回调 AES-256-CBC）。
/// </summary>
public static class FeishuCrypto
{
    /// <summary>
    /// 计算自定义机器人签名：以 <c>{timestamp}\n{secret}</c> 为键、空消息做 HMAC-SHA256 后 Base64。
    /// </summary>
    public static string SignWebhook(long timestampSeconds, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        var key = Encoding.UTF8.GetBytes($"{timestampSeconds}\n{secret}");

        using var hmac = new HMACSHA256(key);
        var hash = hmac.ComputeHash([]);

        return Convert.ToBase64String(hash);
    }

    /// <summary>当前秒级时间戳。</summary>
    public static long TimestampNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>
    /// 解密事件回调内容：<c>encrypt</c> 为 Base64(16 字节 IV + 密文)，
    /// 密钥为 <c>SHA256(encryptKey)</c>，AES-256-CBC + PKCS7。
    /// </summary>
    public static string DecryptEvent(string encryptKey, string encrypt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(encrypt);

        var buffer = Convert.FromBase64String(encrypt);
        if (buffer.Length < 17)
            throw new FormatException("encrypt 内容过短，不是合法的飞书加密载荷");

        var key = SHA256.HashData(Encoding.UTF8.GetBytes(encryptKey));
        var iv = buffer[..16];
        var cipher = buffer[16..];

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        var plain = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);

        return Encoding.UTF8.GetString(plain);
    }
}
