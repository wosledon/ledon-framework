using System.Net;
using System.Net.Sockets;

namespace Ledon.Web.Tests;

/// <summary>
/// 测试端口分配器。
/// <para>
/// "绑定 0 端口 → 读端口 → 关闭" 这个经典写法在并行测试里会撞车：两个测试类
/// 可能拿到同一个端口，后一个 bind 直接 WSAEACCES/EADDRINUSE（表现为偶发失败）。
/// 这里用一把锁 + 已发放集合把进程内的分配串起来，从根上消掉这类抖动。
/// </para>
/// </summary>
internal static class TestPorts
{
    private static readonly Lock Gate = new();
    private static readonly HashSet<int> Issued = [];

    /// <summary>分配一个当前没人用的端口（TCP 与 UDP 都能绑上）。</summary>
    public static int Next()
    {
        lock (Gate)
        {
            for (var attempt = 0; attempt < 200; attempt++)
            {
                var candidate = Probe();

                if (Issued.Add(candidate))
                    return candidate;
            }

            throw new InvalidOperationException("连续 200 次都没找到空闲端口");
        }
    }

    /// <summary>让操作系统挑一个空闲端口（探完就还回去，靠 Issued 集合防止重复发放）。</summary>
    private static int Probe()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
