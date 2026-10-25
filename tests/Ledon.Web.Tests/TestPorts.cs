using System.Net;
using System.Net.Sockets;

namespace Ledon.Web.Tests;

/// <summary>
/// 测试端口分配器：给每个测试类发一段**预留**的连续端口。
/// <para>
/// 为什么不用"绑 0 端口 → 读端口 → 关闭"：那个端口在关闭后就还给操作系统了，
/// 到测试真正 bind 之间，OS 可能把它发给任何别的连接（测试自己的 HttpClient、
/// DNS、并行类的探测……）。测试越多、并行度越高，撞车概率越高——表现为
/// 偶发的 WSAEACCES/EADDRINUSE，而且每次都不一样，极难排查。
/// </para>
/// <para>
/// 改成：进程内静态分配一段端口（远低于 Windows 的临时端口区间 49152-65535，
/// 也低于 Linux 的 32768-60999），同一段只发一个测试类。跨类不可能撞，
/// 和操作系统要端口也不会撞。
/// </para>
/// </summary>
internal static class TestPorts
{
    /// <summary>分配区间起点（避开各平台默认的 ephemeral 区间）。</summary>
    private const int RangeStart = 21000;

    /// <summary>分配区间终点。</summary>
    private const int RangeEnd = 28000;

    /// <summary>每个测试类拿一段（足够覆盖一个类里全部 socket 需求）。</summary>
    private const int BlockSize = 12;

    private static readonly Lock Gate = new();

    /// <summary>已发放的段的起点（判断重叠用）。</summary>
    private static readonly HashSet<int> Reserved = [];

    /// <summary>下一个待考察的段起点。</summary>
    private static int _cursor = RangeStart;

    /// <summary>预留一段端口，返回段对象。</summary>
    public static TestPortBlock Reserve() => new(ReserveBlock());

    private static int ReserveBlock()
    {
        lock (Gate)
        {
            while (_cursor + BlockSize <= RangeEnd)
            {
                var candidate = _cursor;
                _cursor += BlockSize;

                if (Reserved.Add(candidate))
                    return candidate;
            }

            throw new InvalidOperationException(
                $"测试端口区间 [{RangeStart}, {RangeEnd}) 已用尽（每段 {BlockSize} 个）。" +
                "请调大区间或让测试类复用端口。");
        }
    }
}

/// <summary>
/// 一段预留给单个测试类的端口。用法：
/// <code>
/// private readonly TestPortBlock _ports = TestPorts.Reserve();
/// private int Port => _ports[0];       // 每换一个下标就是另一个端口
/// </code>
/// </summary>
internal readonly struct TestPortBlock(int basePort)
{
    private readonly int _basePort = basePort;

    /// <summary>取段内第 <paramref name="index"/> 个端口。</summary>
    public int this[int index] => _basePort + index;

    /// <summary>段内端口数量。</summary>
    public int Size => 12;

    public override string ToString() => $"{_basePort}-{_basePort + Size - 1}";
}
