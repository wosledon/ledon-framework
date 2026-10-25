using System.Net.Sockets;

namespace Ledon.Protocols;

/// <summary>
/// TCP 套接字的跨平台调优：客户端与服务端共用（两边都引用了本包）。
/// </summary>
public static class TcpTuning
{
    /// <summary>
    /// 关闭 Nagle（小帧请求/响应协议不被 40ms 延迟拖住）并按需打开 TCP keepalive。
    /// 任何一项设置失败都只忽略：调优失败不该让连接建不起来。
    /// </summary>
    /// <param name="client">已连接的客户端。</param>
    /// <param name="noDelay">是否禁用 Nagle。</param>
    /// <param name="keepAlive">keepalive 间隔；<see cref="TimeSpan.Zero"/> 表示不启用。</param>
    public static void Apply(TcpClient client, bool noDelay, TimeSpan keepAlive)
    {
        try
        {
            client.NoDelay = noDelay;
        }
        catch (SocketException)
        {
            // 连接已断开时设置无效：忽略
        }

        if (keepAlive > TimeSpan.Zero)
        {
            try
            {
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            }
            catch (SocketException)
            {
                // 平台不支持：忽略（应用层空闲超时仍会兜底）
            }
        }
    }
}
