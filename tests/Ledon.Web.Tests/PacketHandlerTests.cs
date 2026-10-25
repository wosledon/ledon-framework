using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Ledon.Protocols;
using Ledon.Sockets.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ledon.Web.Tests;

/// <summary>
/// 类型化分发（PacketHandlerRegistry）与协议客户端连接的约定注册。
/// 这些用例对应真实踩过的坑：struct 包被静默跳过、并发连接把内部信号量撑爆。
/// </summary>
public class PacketHandlerTests
{
    [Fact]
    public void Struct_packets_are_discovered_by_the_convention_scan()
    {
        // 回归：[PacketType] 的 AttributeUsage 明确允许 Struct，但扫描器曾只判 IsClass，
        // 导致所有 struct 包被静默跳过——直到运行时才以"未注册类型标识"炸出来
        PacketHandlerRegistry.Clear();

        var services = new ServiceCollection();
        services.AddLedonPacketHandlers(bigEndian: true);

        Assert.Contains((ushort)42, PacketHandlerRegistry.TypeIds);
    }

    [Fact]
    public async Task Concurrent_connect_does_not_overflow_the_signal()
    {
        // 回归：连接信号量容量 1，并发 ConnectAsync 重复释放会抛 SemaphoreFullException
        var client = new Ledon.Sockets.Client.TcpProtocolClient(
            new Ledon.Sockets.Client.ProtocolClientOptions
            {
                Host = "127.0.0.1",
                Port = FreePort(),   // 没人监听：连接必然失败
                AutoReconnect = false,
            },
            new LengthPrefixedFrameCodec(),
            new NoopHandler(),
            NullLogger<Ledon.Sockets.Client.TcpProtocolClient>.Instance);

        var tasks = Enumerable.Range(0, 4)
            .Select(_ => client.ConnectAsync())
            .ToArray();

        // 失败原因必须是"连不上"（连接被拒），而不是内部信号量溢出
        var error = await Assert.ThrowsAsync<SocketException>(() => Task.WhenAll(tasks));

        Assert.DoesNotContain("Semaphore", error.Message, StringComparison.OrdinalIgnoreCase);

        await client.DisposeAsync();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class NoopHandler : Ledon.Sockets.Client.ClientHandlerBase;

    /// <summary>struct 包（曾被扫描器漏掉的那一类）。</summary>
    [PacketType(42)]
    public struct SamplePacket
    {
        public SamplePacket()
        {
        }

        [PacketField(0, PacketFieldKind.UInt32, BigEndian = true)]
        public uint Version;

        [PacketField(1, PacketFieldKind.Utf8String, BigEndian = true)]
        public string User = "";
    }

    public sealed class SamplePacketHandler : IPacketHandler<SamplePacket>
    {
        public Task HandleAsync(
            ConnectionContext connection, SamplePacket packet, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
