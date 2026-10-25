using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Ledon.Streaming.Media;
using Ledon.Streaming.Playback;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ledon.Streaming.Rtmp;

/// <summary>RTMP 服务端选项。</summary>
public sealed class RtmpServerOptions
{
    /// <summary>监听地址，默认 <c>0.0.0.0</c>。</summary>
    public string Address { get; set; } = "0.0.0.0";

    /// <summary>监听端口，默认 1935（RTMP 标准端口）。</summary>
    public int Port { get; set; } = 1935;

    /// <summary>出站块大小（我们发给对端的 chunk size），默认 4096。</summary>
    public int OutgoingChunkSize { get; set; } = 4096;

    /// <summary>接收窗口（字节），默认 2500000。</summary>
    public int WindowAcknowledgementSize { get; set; } = 2_500_000;
}

/// <summary>
/// RTMP 服务端：完整的推流/拉流服务器。
/// <list type="bullet">
///   <item>握手（C0/C1/C2 ↔ S0/S1/S2）；</item>
///   <item>命令处理：connect / releaseStream / FCPublish / createStream / publish /
///         play / FCUnpublish / deleteStream / closeStream；</item>
///   <item>媒体转发：发布者的音视频 Tag 写入 <see cref="StreamHub"/>，
///         订阅者从 hub 读取并编码回 RTMP 分块流；</item>
///   <item>每连接一个独立读取循环 + DI 作用域，互不阻塞。</item>
/// </list>
/// </summary>
public sealed class RtmpServer : BackgroundService
{
    private readonly RtmpServerOptions _options;
    private readonly StreamHub _hub;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RtmpServer> _logger;
    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<int, RtmpConnection> _connections = new();

    private int _connectionSeq;

    public RtmpServer(
        RtmpServerOptions options,
        StreamHub hub,
        IServiceScopeFactory scopeFactory,
        ILogger<RtmpServer> logger)
    {
        _options = options;
        _hub = hub;
        _scopeFactory = scopeFactory;
        _logger = logger;

        _listener = new TcpListener(IPAddress.Parse(options.Address), options.Port);
    }

    /// <summary>当前连接数。</summary>
    public int ConnectionCount => _connections.Count;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _listener.Start(128);
        _logger.LogInformation("RTMP 服务端已监听 {Address}:{Port}", _options.Address, _options.Port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);

                var id = Interlocked.Increment(ref _connectionSeq);
                var connection = new RtmpConnection(id, client, _options, _hub, _scopeFactory, _logger);

                if (_connections.TryAdd(id, connection))
                    _ = Task.Run(() => connection.ServeAsync(stoppingToken), CancellationToken.None);
                else
                    client.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            // 宿主持停
        }
        finally
        {
            _listener.Stop();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        foreach (var connection in _connections.Values)
            connection.Close();

        _connections.Clear();
    }
}

/// <summary>一条 RTMP 连接的状态机。</summary>
internal sealed class RtmpConnection
{
    private readonly int _id;
    private readonly TcpClient _client;
    private readonly RtmpServerOptions _options;
    private readonly StreamHub _hub;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;

    private readonly RtmpChunkDecoder _decoder = new();
    private readonly Lock _sendGate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ArrayBufferWriter<byte> _outbound = new();
    private RtmpChunkEncoder? _encoder;

    private StreamIdentity? _publishing;
    private StreamIdentity? _playing;
    private Guid _playingSubscription;

    public RtmpConnection(
        int id,
        TcpClient client,
        RtmpServerOptions options,
        StreamHub hub,
        IServiceScopeFactory scopeFactory,
        ILogger logger)
    {
        _id = id;
        _client = client;
        _options = options;
        _hub = hub;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task ServeAsync(CancellationToken cancellationToken)
    {
        var remote = _client.Client.RemoteEndPoint?.ToString() ?? "unknown";

        try
        {
            await using var stream = _client.GetStream();

            // 1) 握手
            await HandshakeAsync(stream, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("RTMP 连接 {Id} 握手成功（{Remote}）", _id, remote);

            // 2) 分块流循环
            var buffer = new byte[8192];
            var pending = new byte[1024 * 1024];
            var pendingLength = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                if (pendingLength + read > pending.Length)
                    throw new InvalidOperationException("RTMP 缓冲溢出");

                buffer.AsSpan(0, read).CopyTo(pending.AsSpan(pendingLength));
                pendingLength += read;

                var messages = new List<RtmpMessage>();
                pendingLength = _decoder.Decode(pending, pendingLength, messages.Add);

                foreach (var message in messages)
                    await HandleMessageAsync(message, cancellationToken).ConfigureAwait(false);

                await FlushAsync(stream, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "RTMP 连接 {Id} 异常断开", _id);
        }
        finally
        {
            Cleanup();
            _client.Dispose();
        }
    }

    private void Cleanup()
    {
        if (_publishing is not null)
            _hub.Unpublish(_publishing);

        if (_playing is not null && _playingSubscription != Guid.Empty)
            _hub.Unsubscribe(_playing, _playingSubscription);

        _publishing = null;
        _playing = null;
    }

    public void Close()
    {
        try
        {
            _client.Close();
        }
        catch (SocketException)
        {
            // 已断开
        }
    }

    private async Task HandshakeAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        // C0 + C1（1537 字节）
        var c0c1 = new byte[RtmpHandshake.C0C1Size];
        var read = 0;
        while (read < c0c1.Length)
            read += await stream.ReadAsync(c0c1.AsMemory(read, c0c1.Length - read), cancellationToken).ConfigureAwait(false);

        if (c0c1[0] != RtmpHandshake.Version)
            throw new InvalidOperationException($"不支持的 RTMP 版本 {c0c1[0]}");

        // S0 + S1 + S2
        var s0s1 = RtmpHandshake.CreateS0S1();
        var s2 = RtmpHandshake.CreateS2(c0c1.AsSpan(1));

        await stream.WriteAsync(s0s1, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(s2, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        // C2（1536 字节，简单模式下回显我们的 S1）
        var c2 = new byte[RtmpHandshake.C2S2Size];
        read = 0;
        while (read < c2.Length)
            read += await stream.ReadAsync(c2.AsMemory(read, c2.Length - read), cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("RTMP 连接 {Id} 握手完成", _id);
    }

    private async Task HandleMessageAsync(RtmpMessage message, CancellationToken cancellationToken)
    {
        // 协议控制消息（类型 ID < 3 的部分）立即处理
        switch (message.MessageType)
        {
            case RtmpMessageType.SetChunkSize:
                if (message.Payload.Length >= 4)
                    _decoder.SetChunkSize((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(message.Payload));
                return;

            case RtmpMessageType.WindowAcknowledgementSize:
                SendControl(RtmpMessageType.WindowAcknowledgementSize, _options.WindowAcknowledgementSize);
                return;

            case RtmpMessageType.SetPeerBandwidth:
                return;

            case RtmpMessageType.CommandAmf0:
                await HandleCommandAsync(message, cancellationToken).ConfigureAwait(false);
                return;

            case RtmpMessageType.Audio:
            case RtmpMessageType.Video:
                HandleMedia(message);
                return;

            case RtmpMessageType.DataAmf0:
                HandleData(message);
                return;
        }
    }

    private async Task HandleCommandAsync(RtmpMessage message, CancellationToken cancellationToken)
    {
        var reader = new Amf0Reader(message.Payload);
        var command = reader.ReadValue() as string ?? "";
        // 命令布局统一为：命令名, transactionId, commandObject(通常 null), ...具体参数
        var transactionId = ReadNumber(ref reader);
        reader.ReadValue();   // command object（连接信息/发布参数都在这里，或为 null）

        switch (command)
        {
            case "connect":
                // Amf0Reader 是 ref struct：不能作为异步方法参数，先同步取出需要的信息
                HandleConnect(message.StreamId, transactionId);
                break;

            case "releaseStream":
            case "FCPublish":
                SendResult(message.StreamId, transactionId);
                break;

            case "createStream":
                SendResult(message.StreamId, transactionId, 1);
                break;

            case "publish":
                HandlePublish(reader);
                SendStatus(message.StreamId, "NetStream.Publish.Start", "Publishing");
                break;

            case "play":
                HandlePlay(ReadPlayName(reader), message.StreamId);
                break;

            case "FCUnpublish":
            case "closeStream":
            case "deleteStream":
                Cleanup();
                SendResult(message.StreamId, transactionId);
                break;

            default:
                _logger.LogDebug("RTMP 连接 {Id} 未处理命令 {Command}", _id, command);
                break;
        }
    }

    private void HandleConnect(int streamId, double transactionId)
    {
        // 窗口确认
        SendControl(RtmpMessageType.WindowAcknowledgementSize, _options.WindowAcknowledgementSize);
        SendControl(RtmpMessageType.SetPeerBandwidth, _options.WindowAcknowledgementSize);

        // 协商块大小
        var setChunkSize = new RtmpMessage
        {
            MessageType = RtmpMessageType.SetChunkSize,
            StreamId = 0,
            TimestampMs = 0,
            Payload = BigEndianBytes((uint)_options.OutgoingChunkSize),
        };
        Send(setChunkSize);
        _encoder = new RtmpChunkEncoder(_outbound);
        _encoder.SetChunkSize(_options.OutgoingChunkSize);

        // _result（连接成功）
        SendResult(streamId, transactionId, client: "FMS/3,5,7,7009");
    }

    private void HandlePublish(Amf0Reader reader)
    {
        // reader 已越过命令名/transactionId/commandObject，正对发布名
        var streamName = reader.ReadValue() as string ?? "";   // 发布名（{app}/{name}）

        _publishing = StreamIdentity.Parse(streamName);
        _hub.Publish(_publishing, $"rtmp:{_id}");

        _logger.LogInformation("RTMP 连接 {Id} 发布 {Stream}", _id, streamName);
    }

    /// <summary>从 play 命令里取出流名（同步：ref struct 不能进异步方法）。</summary>
    private static string ReadPlayName(Amf0Reader reader)
    {
        // reader 已越过命令名/transactionId/commandObject，正对流名
        var streamName = reader.ReadValue() as string ?? "";
        reader.ReadValue();               // start
        reader.ReadValue();               // duration（可选）
        reader.ReadValue();               // reset（可选）

        return streamName;
    }

    private void HandlePlay(string streamName, int streamId)
    {
        _playing = StreamIdentity.Parse(streamName);
        _playingSubscription = Guid.Empty;

        // StreamBegin 用户控制
        SendUserControl(0, streamId);

        // NetStream.Play.Reset + NetStream.Play.Start
        SendStatus(streamId, "NetStream.Play.Reset", "Playing");
        SendStatus(streamId, "NetStream.Play.Start", "Playing");

        var stream = _hub.Find(_playing);
        if (stream is null || !stream.IsPublished)
        {
            SendStatus(streamId, "NetStream.Play.StreamNotFound", "StreamNotFound");
            _logger.LogWarning("RTMP 连接 {Id} 请求播放不存在的流 {Stream}", _id, streamName);
        }
        else
        {
            // 订阅：GOP 缓存会自动补发，播放器立刻出画面
            var subscription = stream.Subscribe();
            _playingSubscription = subscription.Id;

            _logger.LogInformation("RTMP 连接 {Id} 播放 {Stream}", _id, streamName);

            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var tag in subscription.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                    {
                        // FLV Tag → RTMP 消息
                        Send(new RtmpMessage
                        {
                            MessageType = tag.TagType switch
                            {
                                Media.FlvTagType.Audio => RtmpMessageType.Audio,
                                Media.FlvTagType.Video => RtmpMessageType.Video,
                                _ => RtmpMessageType.DataAmf0,
                            },
                            StreamId = streamId,
                            TimestampMs = tag.TimestampMs,
                            Payload = tag.Data,
                        });

                        await FlushAsync(_client.GetStream(), CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "RTMP 连接 {Id} 媒体推送失败", _id);
                }
            }, CancellationToken.None);
        }
    }

    private void HandleMedia(RtmpMessage message)
    {
        if (_publishing is null)
            return;

        _hub.Write(_publishing, new Media.FlvTag
        {
            TagType = message.MessageType == RtmpMessageType.Video
                ? Media.FlvTagType.Video
                : Media.FlvTagType.Audio,
            TimestampMs = message.TimestampMs,
            Data = message.Payload,
        });
    }

    private void HandleData(RtmpMessage message)
    {
        // onMetaData 等：转成脚本数据转发给观众（FLV Tag）
        if (_publishing is null)
            return;

        _hub.Write(_publishing, new Media.FlvTag
        {
            TagType = Media.FlvTagType.Script,
            TimestampMs = message.TimestampMs,
            Data = message.Payload,
        });
    }

    private static byte[] BigEndianBytes(uint value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);

        return bytes;
    }

    /// <summary>
    /// 读取 transactionId。必须按 ref 传递：
    /// Amf0Reader 是 ref struct，按值传参会在副本上推进位置，调用方的读取位置不会前进。
    /// </summary>
    private static double ReadNumber(ref Amf0Reader reader)
    {
        var value = reader.ReadValue();

        return value is double number ? number : 0;
    }

    // ---------- 出站消息构造 ----------

    /// <summary>
    /// 写入一条待发消息。与 <see cref="FlushAsync"/> 共用 <see cref="_sendGate"/>：
    /// 媒体推送在独立任务上进行，与主读取循环并发发送，缓冲区必须串行访问。
    /// </summary>
    private void Send(RtmpMessage message)
    {
        lock (_sendGate)
        {
            _encoder ??= new RtmpChunkEncoder(_outbound);
            _encoder.WriteMessage(message);
        }
    }

    private void SendControl(RtmpMessageType type, int value) =>
        Send(new RtmpMessage
        {
            MessageType = type,
            StreamId = 0,
            TimestampMs = 0,
            Payload = BigEndianBytes((uint)value),
        });

    private void SendUserControl(ushort eventType, int streamId)
    {
        var payload = new byte[6];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(payload, eventType);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(2), streamId);

        Send(new RtmpMessage
        {
            MessageType = RtmpMessageType.UserControl,
            StreamId = 0,
            TimestampMs = 0,
            Payload = payload,
        });
    }

    private void SendResult(int streamId, double transactionId, double? streamIdValue = null, string? client = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new Amf0Writer(buffer);
        writer.WriteString("_result");
        writer.WriteNumber(transactionId);

        if (client is not null)
        {
            writer.WriteObjectStart();
            writer.WriteProperty("fmsVer", "FMS/3,5,7,7009");
            writer.WriteProperty("capabilities", 127);
            writer.WriteProperty("mode", 1);
            writer.WriteObjectEnd();
        }
        else
        {
            writer.WriteNull();
        }

        if (streamIdValue is not null)
            writer.WriteNumber(streamIdValue.Value);
        else if (client is not null)
            writer.WriteNull();

        Send(new RtmpMessage
        {
            MessageType = RtmpMessageType.CommandAmf0,
            StreamId = streamId,
            TimestampMs = 0,
            Payload = buffer.WrittenSpan.ToArray(),
        });
    }

    private void SendStatus(int streamId, string code, string level)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new Amf0Writer(buffer);
        writer.WriteString("onStatus");
        writer.WriteNumber(0);
        writer.WriteNull();

        writer.WriteObjectStart();
        writer.WriteProperty("level", level);
        writer.WriteProperty("code", code);
        writer.WriteProperty("description", code);
        writer.WriteObjectEnd();

        Send(new RtmpMessage
        {
            MessageType = RtmpMessageType.CommandAmf0,
            StreamId = streamId,
            TimestampMs = 0,
            Payload = buffer.WrittenSpan.ToArray(),
        });
    }

    private async Task FlushAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        // 写网关横跨"取快照 + 写网络"：媒体推送任务与主读取循环都会调这里，
        // 若只在取快照时加锁，两次 WriteAsync 会交叉发出，字节流即损坏
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] data;
            lock (_sendGate)
            {
                if (_outbound.WrittenCount == 0)
                    return;

                data = _outbound.WrittenSpan.ToArray();
                _outbound.Clear();
            }

            await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
