using System.Buffers.Binary;

namespace Ledon.Streaming.Rtmp;

/// <summary>
/// RTMP 握手（C0/C1/C2 ↔ S0/S1/S2）。RTMP 规范要求先交换 1537 字节，
/// 握手成功后进入分块流阶段。
/// </summary>
public static class RtmpHandshake
{
    /// <summary>C0 + C1 长度（1 + 1536）。</summary>
    public const int C0C1Size = 1537;

    /// <summary>S0 + S1 长度。</summary>
    public const int S0S1Size = 1537;

    /// <summary>C2/S2 长度。</summary>
    public const int C2S2Size = 1536;

    /// <summary>RTMP 版本号（3）。</summary>
    public const byte Version = 3;

    /// <summary>构造 S0+S1（版本号 + 1536 字节：4 字节时间 + 4 字节零 + 1528 字节随机）。</summary>
    public static byte[] CreateS0S1()
    {
        var buffer = new byte[S0S1Size];
        buffer[0] = Version;

        // 时间戳字段填 0（对端不校验）；其余随机数据让握手难以被中继重放
        Random.Shared.NextBytes(buffer.AsSpan(8));

        return buffer;
    }

    /// <summary>
    /// 构造 S2：回显 C1 的内容（RTMP 简单握手模式下对端校验的就是这个）。
    /// </summary>
    public static byte[] CreateS2(ReadOnlySpan<byte> c1)
    {
        if (c1.Length < C2S2Size)
            throw new ArgumentException($"C1 长度不足 {C2S2Size}", nameof(c1));

        var s2 = new byte[C2S2Size];
        c1[..C2S2Size].CopyTo(s2);

        return s2;
    }

    /// <summary>校验 C2：与 S1 相同（简单模式下对端回显我们的 S1）。</summary>
    public static bool ValidateC2(ReadOnlySpan<byte> c2, ReadOnlySpan<byte> s1)
    {
        if (c2.Length < C2S2Size)
            return false;

        return c2[..4].SequenceEqual(s1[..4]) || c2[..C2S2Size].SequenceEqual(s1[..C2S2Size]);
    }
}
