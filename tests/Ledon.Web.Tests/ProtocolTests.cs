using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Ledon.Protocols;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ledon.Web.Tests;

/// <summary>帧编解码与包编解码（协议解析核心逻辑）。</summary>
public class FrameCodecTests
{
    [Fact]
    public void Length_prefixed_splits_multiple_frames()
    {
        var codec = new LengthPrefixedFrameCodec();
        var wire = LengthPrefixedFrameCodec.Encode("ab"u8)
            .Concat(LengthPrefixedFrameCodec.Encode("cde"u8))
            .Concat(LengthPrefixedFrameCodec.Encode("f"u8))
            .ToArray();

        Assert.Equal(["ab", "cde", "f"], ReadAll(codec, wire));
    }

    [Fact]
    public void Length_prefixed_buffers_partial_frames()
    {
        var codec = new LengthPrefixedFrameCodec();
        var wire = LengthPrefixedFrameCodec.Encode("hello"u8);

        // 前两段不完整：一帧都切不出来，字节全部留在缓冲区
        var partial = ReadChunks(codec, wire.AsSpan(0, 5).ToArray(), wire.AsSpan(5, 3).ToArray());
        Assert.Empty(partial.Frames);
        Assert.Equal(8, partial.Remaining);

        // 第三段到齐：切出完整帧，缓冲区清零
        var full = ReadChunks(codec, wire.AsSpan(0, 5).ToArray(), wire.AsSpan(5, 3).ToArray(), wire.AsSpan(8).ToArray());
        Assert.Equal(["hello"], full.Frames);
        Assert.Equal(0, full.Remaining);
    }

    [Fact]
    public void Length_prefixed_big_endian_roundtrips()
    {
        var codec = new LengthPrefixedFrameCodec(bigEndian: true);
        var wire = LengthPrefixedFrameCodec.Encode("xyz"u8, bigEndian: true);

        Assert.Equal(0, wire[0]);   // 大端：首字节为 0
        Assert.Equal(["xyz"], ReadAll(codec, wire));
    }

    [Fact]
    public void Length_prefixed_rejects_negative_length()
    {
        var codec = new LengthPrefixedFrameCodec();
        var wire = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(wire, -1);

        Assert.Throws<ProtocolException>(() => ReadAll(codec, wire));
    }

    [Fact]
    public void Delimiter_splits_lines_and_keeps_remainder()
    {
        var codec = new DelimiterFrameCodec((byte)'\n');

        var (frames, remaining) = ReadChunks(codec, "a\nbb\ncc"u8.ToArray());

        Assert.Equal(["a", "bb"], frames);
        Assert.Equal(2, remaining);   // 末尾没有换行：cc 留着等下一块数据
    }

    [Fact]
    public void Delimiter_supports_multi_byte_separator()
    {
        var codec = new DelimiterFrameCodec("\r\n"u8);

        Assert.Equal(["a", "b"], ReadAll(codec, "a\r\nb\r\nc"u8.ToArray()));
    }

    [Fact]
    public void Fixed_size_splits_exact_chunks()
    {
        var codec = new FixedSizeFrameCodec(4);

        Assert.Equal(["AAAA", "BBBB"], ReadAll(codec, "AAAABBBBCC"u8.ToArray()));
    }

    [Fact]
    public void Text_codec_roundtrips()
    {
        var codec = new TextPacketCodec();

        Assert.Equal("你好", codec.Decode(codec.Encode("你好")));
    }

    [Fact]
    public void Binary_packet_roundtrips_with_header()
    {
        var codec = new BinaryPacketCodec(magic: 0x4C42, version: 1);

        var packet = codec.Decode(codec.Encode(new BinaryPacket(7, 99, [1, 2, 3])));

        Assert.Equal((byte)7, packet.Type);
        Assert.Equal(99u, packet.Sequence);
        Assert.Equal([1, 2, 3], packet.Payload);
    }

    [Fact]
    public void Binary_packet_rejects_wrong_magic()
    {
        var codec = new BinaryPacketCodec(magic: 0x4C42, version: 1);
        var frame = codec.Encode(new BinaryPacket(1, 1, []));
        frame[0] = 0x00;

        Assert.Throws<ProtocolException>(() => codec.Decode(frame));
    }

    [Fact]
    public void Binary_packet_rejects_wrong_version()
    {
        var codec = new BinaryPacketCodec(magic: 0x4C42, version: 1);
        var frame = codec.Encode(new BinaryPacket(1, 1, []));
        frame[2] = 9;

        Assert.Throws<ProtocolException>(() => codec.Decode(frame));
    }

    [Fact]
    public void Binary_packet_rejects_truncated_payload()
    {
        var codec = new BinaryPacketCodec(magic: 0x4C42, version: 1);
        var frame = codec.Encode(new BinaryPacket(1, 1, [1, 2, 3]));

        Assert.Throws<ProtocolException>(() => codec.Decode(frame.AsSpan(0, frame.Length - 1)));
    }

    [Fact]
    public void Binary_packet_rejects_short_header()
    {
        var codec = new BinaryPacketCodec(magic: 0x4C42, version: 1);

        Assert.Throws<ProtocolException>(() => codec.Decode([1, 2, 3]));
    }

    private static List<string> ReadAll(IFrameCodec codec, byte[] wire) =>
        ReadChunks(codec, wire).Frames;

    /// <summary>按块喂给编解码器（模拟网络分包），收集切出的帧与剩余字节。</summary>
    private static (List<string> Frames, int Remaining) ReadChunks(IFrameCodec codec, params byte[][] chunks)
    {
        var buffer = new byte[4096];
        var pending = 0;
        var frames = new List<string>();

        foreach (var chunk in chunks)
        {
            chunk.CopyTo(buffer.AsSpan(pending));
            pending += chunk.Length;

            pending = codec.TryReadFrames(buffer, pending, frame => frames.Add(Encoding.UTF8.GetString(frame)));
        }

        return (frames, pending);
    }
}

/// <summary>TCP 服务端：真实连接、粘包处理、回显、断开清理。</summary>
public class TcpServerTests : IAsyncDisposable
{
    private readonly int _port = GetFreePort();
    private readonly List<IHostedService> _services = [];

    [Fact]
    public async Task Server_echoes_frames_over_real_connection()
    {
        var recorder = new FrameRecorder();
        StartServer(new LengthPrefixedFrameCodec(), recorder);

        using var client = await ConnectWithRetryAsync(_port, TimeSpan.FromSeconds(10));

        await using var stream = client.GetStream();
        await stream.WriteAsync(LengthPrefixedFrameCodec.Encode("ping"u8));
        await stream.FlushAsync();

        Assert.Equal("pong:ping", Encoding.UTF8.GetString(await ReadFrameAsync(stream)));
        Assert.Contains("ping", recorder.Received);
    }

    [Fact]
    public async Task Server_reassembles_split_frames()
    {
        StartServer(new LengthPrefixedFrameCodec(), new FrameRecorder());

        using var client = await ConnectWithRetryAsync(_port, TimeSpan.FromSeconds(10));
        await using var stream = client.GetStream();

        var wire = LengthPrefixedFrameCodec.Encode("hello"u8);

        // 分三次写：模拟网络分包（粘包处理的真正考验）
        await stream.WriteAsync(wire.AsMemory(0, 2));
        await stream.FlushAsync();
        await Task.Delay(50);
        await stream.WriteAsync(wire.AsMemory(2, 3));
        await stream.FlushAsync();
        await Task.Delay(50);
        await stream.WriteAsync(wire.AsMemory(5));
        await stream.FlushAsync();

        Assert.Equal("pong:hello", Encoding.UTF8.GetString(await ReadFrameAsync(stream)));
    }

    [Fact]
    public async Task Server_handles_multiple_frames_in_one_write()
    {
        StartServer(new LengthPrefixedFrameCodec(), new FrameRecorder());

        using var client = await ConnectWithRetryAsync(_port, TimeSpan.FromSeconds(10));
        await using var stream = client.GetStream();

        var wire = LengthPrefixedFrameCodec.Encode("a"u8)
            .Concat(LengthPrefixedFrameCodec.Encode("b"u8))
            .ToArray();
        await stream.WriteAsync(wire);
        await stream.FlushAsync();

        Assert.Equal("pong:a", Encoding.UTF8.GetString(await ReadFrameAsync(stream)));
        Assert.Equal("pong:b", Encoding.UTF8.GetString(await ReadFrameAsync(stream)));
    }

    [Fact]
    public async Task Disconnect_fires_handler_cleanup()
    {
        var recorder = new FrameRecorder();
        StartServer(new LengthPrefixedFrameCodec(), recorder);

        var client = await ConnectWithRetryAsync(_port, TimeSpan.FromSeconds(10));
        client.Close();

        await WaitUntilAsync(() => recorder.Disconnected > 0, TimeSpan.FromSeconds(5));
        Assert.Equal(1, recorder.Disconnected);
    }

    private void StartServer(IFrameCodec codec, IConnectionHandler handler)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedonTcpServer(o => o.Port = _port, codec, handler);

        var provider = services.BuildServiceProvider();

        foreach (var hosted in provider.GetServices<IHostedService>())
        {
            hosted.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            _services.Add(hosted);
        }
    }

    /// <summary>连接并重试：服务端监听就绪前连接会被拒，等它就绪即可。</summary>
    private static async Task<TcpClient> ConnectWithRetryAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;

        while (DateTime.UtcNow < deadline)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                return client;
            }
            catch (Exception ex)
            {
                last = ex;
                client.Dispose();
                await Task.Delay(50);
            }
        }

        throw new TimeoutException($"连接 127.0.0.1:{port} 超时：{last?.Message}");
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream)
    {
        var header = new byte[4];
        var read = 0;
        while (read < 4)
            read += await stream.ReadAsync(header.AsMemory(read, 4 - read));

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        var payload = new byte[length];
        var offset = 0;
        while (offset < length)
            offset += await stream.ReadAsync(payload.AsMemory(offset, length - offset));

        return payload;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return port;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var hosted in _services)
            await hosted.StopAsync(CancellationToken.None);

        GC.SuppressFinalize(this);
    }

    private sealed class FrameRecorder : ConnectionHandlerBase
    {
        private readonly List<string> _received = [];
        private int _disconnected;

        public IReadOnlyList<string> Received
        {
            get
            {
                lock (_received)
                    return _received.ToArray();
            }
        }

        public int Disconnected => Volatile.Read(ref _disconnected);

        public override async Task OnFrameAsync(ConnectionContext connection, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
        {
            var text = Encoding.UTF8.GetString(frame.Span);

            lock (_received)
                _received.Add(text);

            await connection.SendAsync(
                LengthPrefixedFrameCodec.Encode(Encoding.UTF8.GetBytes($"pong:{text}")),
                cancellationToken);
        }

        public override Task OnDisconnectedAsync(ConnectionContext connection, Exception? exception, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _disconnected);
            return Task.CompletedTask;
        }
    }
}
