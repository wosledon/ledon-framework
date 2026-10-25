using System.Text;
using Ledon.Protocols;

namespace Ledon.Sip;

/// <summary>
/// SIP over TCP/TLS 的帧编解码：按 Content-Length 切帧（RFC 3261 §18.3）。
/// <para>
/// TCP 是字节流，没有"一条消息"的边界：必须先找到 <c>\r\n\r\n</c> 拿到头部，
/// 从头部读 Content-Length，再等 body 收全。放在 <c>Ledon.Sip</c> 里是因为
/// 客户端和服务端都要用它，而它本质是协议格式而不是服务端策略。
/// </para>
/// </summary>
public sealed class SipTcpFrameCodec : IFrameCodec
{
    /// <inheritdoc />
    public int TryReadFrames(scoped Span<byte> buffer, int length, Action<ReadOnlySpan<byte>> onFrame)
    {
        var offset = 0;

        while (offset < length)
        {
            // 1) 找头部结束（\r\n\r\n）
            var window = buffer.Slice(offset, length - offset);
            var headerEnd = window.IndexOf("\r\n\r\n"u8);
            if (headerEnd < 0)
                break;   // 头部还没收全

            var bodyStart = headerEnd + 4;

            // 2) 从头部解析 Content-Length（大小写不敏感）
            var contentLength = ParseContentLength(window.Slice(0, headerEnd));
            if (contentLength < 0)
                throw new ProtocolException("SIP 头部的 Content-Length 非法（协议不同步）");

            // 3) 判断 body 是否收全
            if (length - offset - bodyStart < contentLength)
                break;

            var total = bodyStart + contentLength;
            onFrame(window.Slice(0, total));
            offset += total;
        }

        if (offset > 0)
            buffer.Slice(offset, length - offset).CopyTo(buffer);

        return length - offset;
    }

    /// <summary>从头部读 Content-Length；没有该头按 0 处理（合法：无 body 的消息可以省略）。</summary>
    private static int ParseContentLength(ReadOnlySpan<byte> header)
    {
        var index = 0;

        while (true)
        {
            var lineStart = header.Slice(index);
            var nameEnd = lineStart.IndexOf("Content-Length:"u8);

            if (nameEnd < 0)
                return 0;

            // 确认匹配到的是行首（前面必须是 \r\n），避免匹配到 "X-Content-Length:"
            if (index == 0 || (index >= 2 && header[index - 2] == '\r' && header[index - 1] == '\n'))
            {
                var value = lineStart.Slice(nameEnd + "Content-Length:".Length);
                var end = value.IndexOf((byte)'\r');
                if (end < 0)
                    end = value.IndexOf((byte)'\n');
                if (end >= 0)
                    value = value.Slice(0, end);

                // 头字段值是 ASCII 数字：直接转字符串解析（切帧时才走一次）
                return int.TryParse(Encoding.ASCII.GetString(value).Trim(), out var length)
                    ? length
                    : throw new ProtocolException($"SIP Content-Length 不是数字：{Encoding.ASCII.GetString(value).Trim()}");
            }

            index += nameEnd + 1;
        }
    }
}
