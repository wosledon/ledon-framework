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

    /// <summary>
    /// 保活间隔，默认 30 秒：定期发 PingRequest 并等 PingResponse。
    /// 拔网线/进程被杀的对端不会走 TCP 关闭流程，只有心跳能发现——否则连接永远挂着。
    /// 设为 <see cref="TimeSpan.Zero"/> 关闭。
    /// </summary>
    public TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 空闲超时，默认 90 秒：多久没收到任何字节就断开。
    /// 只连上不发命令的连接（扫描器常用）不该永远占资源。
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>最大并发连接数，默认 1000；超出直接拒绝新连接。</summary>
    public int MaxConnections { get; set; } = 1000;

    /// <summary>
    /// 是否要求推流令牌，默认 <c>false</c>（与常见流媒体服务器的默认行为一致，
    /// 开箱即可用）。**生产环境必须显式打开**，否则任何人都能往你的流上推。
    /// 令牌从推流 URL 的查询串或 connect 命令对象里取，交给
    /// <see cref="IRtmpAuthenticator"/> 校验；校验不过回 NetStream.Failed 并断开。
    /// </summary>
    public bool RequirePublishToken { get; set; }

    /// <summary>是否要求拉流令牌，默认 <c>false</c>（内网/公开直播可关）。</summary>
    public bool RequirePlayToken { get; set; }
}

/// <summary>
/// RTMP 鉴权钩子：推流/拉流前的最后一道关。
/// 默认 <see cref="AllowAllRtmpAuthenticator"/>（不鉴权）；业务实现
/// <see cref="IRtmpAuthenticator"/> 并注册进容器即可接管。
/// </summary>
public interface IRtmpAuthenticator
{
    /// <summary>校验推流。返回 false 时连接会被拒绝。</summary>
    ValueTask<bool> AuthorizePublishAsync(RtmpAuthContext context, CancellationToken cancellationToken = default);

    /// <summary>校验拉流。返回 false 时连接会被拒绝。</summary>
    ValueTask<bool> AuthorizePlayAsync(RtmpAuthContext context, CancellationToken cancellationToken = default);
}

/// <summary>一次鉴权的上下文（连接信息 + 令牌来源）。</summary>
public sealed class RtmpAuthContext
{
    /// <summary>应用名（rtmp://host/{app}/{name} 的 app 部分）。</summary>
    public required string App { get; init; }

    /// <summary>流名（不含 app 前缀）。</summary>
    public required string StreamName { get; init; }

    /// <summary>令牌（URL 查询串或 connect 对象里的 token 字段；没有则空串）。</summary>
    public string Token { get; init; } = "";

    /// <summary>远端地址。</summary>
    public string? RemoteEndPoint { get; init; }

    /// <summary>connect 命令对象里的全部字段（app/tcUrl/flashVer/...）。</summary>
    public IReadOnlyDictionary<string, string> ConnectionProperties { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>默认鉴权器：全部放行（仅用于内网/测试）。</summary>
public sealed class AllowAllRtmpAuthenticator : IRtmpAuthenticator
{
    public ValueTask<bool> AuthorizePublishAsync(RtmpAuthContext context, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(true);

    public ValueTask<bool> AuthorizePlayAsync(RtmpAuthContext context, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(true);
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
    private readonly IStreamRegistry _registry;
    private readonly StreamEventDispatcher _dispatcher;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RtmpServer> _logger;
    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<int, RtmpConnection> _connections = new();

    private int _connectionSeq;

    public RtmpServer(
        RtmpServerOptions options,
        StreamHub hub,
        IStreamRegistry registry,
        StreamEventDispatcher dispatcher,
        IServiceScopeFactory scopeFactory,
        ILogger<RtmpServer> logger)
    {
        _options = options;
        _hub = hub;
        _registry = registry;
        _dispatcher = dispatcher;
        _scopeFactory = scopeFactory;
        _logger = logger;

        _listener = new TcpListener(IPAddress.Parse(options.Address), options.Port);
    }

    /// <summary>当前连接数。</summary>
    public int ConnectionCount => _connections.Count;

    /// <summary>累计拒绝的连接数（超过 <see cref="RtmpServerOptions.MaxConnections"/>）。</summary>
    public long RejectedConnections { get; private set; }

    /// <summary>断开推/拉指定流的连接；返回被断开的连接数。</summary>
    public int Kickoff(StreamIdentity identity, string? clientId = null)
    {
        var kicked = 0;

        foreach (var connection in _connections.Values)
        {
            if (!connection.Matches(identity, clientId))
                continue;

            connection.Close();
            kicked++;
        }

        return kicked;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _listener.Start(128);
        _logger.LogInformation("RTMP 服务端已监听 {Address}:{Port}", _options.Address, _options.Port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient client;

                try
                {
                    client = await _listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;   // 宿主持停
                }
                catch (Exception ex)
                {
                    // accept 失败不能让监听器停摆，否则服务端会毫无提示地永久停止接受
                    _logger.LogError(ex, "RTMP 接受连接失败（继续监听）");
                    await Task.Delay(TimeSpan.FromMilliseconds(100), stoppingToken).ConfigureAwait(false);

                    continue;
                }

                if (_connections.Count >= _options.MaxConnections)
                {
                    RejectedConnections++;
                    _logger.LogWarning("RTMP 连接数已达上限 {Max}，拒绝 {Remote}",
                        _options.MaxConnections, client.Client.RemoteEndPoint);

                    client.Dispose();

                    continue;
                }

                var id = Interlocked.Increment(ref _connectionSeq);

                // 每连接一个 DI 作用域：鉴权/业务处理器可以按连接解析（连接结束时释放）
                var scope = _scopeFactory.CreateAsyncScope();
                var authenticator = scope.ServiceProvider.GetService<IRtmpAuthenticator>()
                    ?? new AllowAllRtmpAuthenticator();

                var connection = new RtmpConnection(id, client, _options, _hub, _registry, _dispatcher, scope, authenticator, _logger);

                if (_connections.TryAdd(id, connection))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await connection.ServeAsync(stoppingToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "RTMP 连接 {Id} 异常结束", id);
                        }
                        finally
                        {
                            // 连接结束必须从字典移除：否则 ConnectionCount 只增不减，最终触顶拒绝新连接
                            _connections.TryRemove(id, out _);
                        }
                    }, CancellationToken.None);
                }
                else
                {
                    client.Dispose();
                    _ = scope.DisposeAsync();
                }
            }
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
    private readonly IStreamRegistry _registry;
    private readonly StreamEventDispatcher _dispatcher;
    private readonly AsyncServiceScope _scope;
    private readonly IRtmpAuthenticator _authenticator;
    private readonly ILogger _logger;
    private readonly string _remote;
    private readonly string _remoteAddress;

    private readonly RtmpChunkDecoder _decoder = new();
    private readonly Lock _sendGate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ArrayBufferWriter<byte> _outbound = new();
    private RtmpChunkEncoder? _encoder;

    private StreamIdentity? _publishing;
    private StreamIdentity? _playing;
    private Guid _playingSubscription;
    private IReadOnlyDictionary<string, string> _connectionProperties = new Dictionary<string, string>();
    private long _lastHeartbeatTicks;

    /// <summary>连接标识（控制面踢人/查询用）。</summary>
    public string ClientId => _id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public bool Matches(StreamIdentity identity, string? clientId)
    {
        if (!string.IsNullOrEmpty(clientId))
            return string.Equals(ClientId, clientId, StringComparison.Ordinal);

        return string.Equals(_publishing?.Path, identity.Path, StringComparison.OrdinalIgnoreCase)
            || string.Equals(_playing?.Path, identity.Path, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>去掉流名上的查询串（live/cam?token=x → live/cam）。</summary>
    private static string StripQuery(string streamName)
    {
        var query = streamName.IndexOf('?');
        return query < 0 ? streamName : streamName[..query];
    }

    private static Dictionary<string, string> ParseQuery(string streamName)
    {
        var queryIndex = streamName.IndexOf('?');

        if (queryIndex < 0 || queryIndex == streamName.Length - 1)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return streamName[(queryIndex + 1)..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0]),
                parts => Uri.UnescapeDataString(parts[1]),
                StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string> MergeParameters(
        IReadOnlyDictionary<string, string> properties,
        Dictionary<string, string> query)
    {
        if (properties.Count == 0)
            return query;

        var merged = new Dictionary<string, string>(properties, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in query)
            merged[key] = value;

        return merged;
    }

    private RtmpAuthContext NewContext(StreamIdentity identity, string token) => new()
    {
        App = identity.App,
        StreamName = identity.Name,
        Token = token,
        RemoteEndPoint = _remote,
        ConnectionProperties = _connectionProperties,
    };

    /// <summary>有数据流动就顺带刷新注册表心跳（节流到 5 秒一次），避免活跃流被离线扫描误杀。</summary>
    private void TouchIfDue()
    {
        if (_publishing is null)
            return;

        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastHeartbeatTicks);

        if (now - last < 5_000)
            return;

        if (Interlocked.CompareExchange(ref _lastHeartbeatTicks, now, last) != last)
            return;

        _registry.OnHeartbeat(_publishing);
    }

    public RtmpConnection(
        int id,
        TcpClient client,
        RtmpServerOptions options,
        StreamHub hub,
        IStreamRegistry registry,
        StreamEventDispatcher dispatcher,
        AsyncServiceScope scope,
        IRtmpAuthenticator authenticator,
        ILogger logger)
    {
        _id = id;
        _client = client;
        _options = options;
        _hub = hub;
        _registry = registry;
        _dispatcher = dispatcher;
        _scope = scope;
        _authenticator = authenticator;
        _logger = logger;
        _remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        _remoteAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
    }

    public async Task ServeAsync(CancellationToken cancellationToken)
    {
        // 连接级生命周期：空闲超时/保活失败只取消这一条连接
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            await using var stream = _client.GetStream();

            // 1) 握手
            await HandshakeAsync(stream, lifetime.Token).ConfigureAwait(false);

            _logger.LogInformation("RTMP 连接 {Id} 握手成功（{Remote}）", _id, _remote);

            // 2) 保活：定期 PingRequest，拔网线的对端只有心跳能发现
            using var pingTimer = _options.PingInterval > TimeSpan.Zero
                ? new PeriodicTimer(_options.PingInterval)
                : null;

            _ = pingTimer is null
                ? Task.CompletedTask
                : Task.Run(() => PingLoopAsync(stream, pingTimer, lifetime), CancellationToken.None);

            // 3) 分块流循环
            var buffer = new byte[8192];
            var pending = new byte[1024 * 1024];
            var pendingLength = 0;

            while (!lifetime.IsCancellationRequested)
            {
                // 空闲超时：每次读前重新武装
                using var idle = _options.IdleTimeout > TimeSpan.Zero
                    ? CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token)
                    : null;

                idle?.CancelAfter(_options.IdleTimeout);

                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, idle?.Token ?? lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"连接空闲超过 {_options.IdleTimeout.TotalSeconds} 秒");
                }

                if (read == 0)
                    break;

                TouchIfDue();

                if (pendingLength + read > pending.Length)
                    throw new InvalidOperationException("RTMP 缓冲溢出");

                buffer.AsSpan(0, read).CopyTo(pending.AsSpan(pendingLength));
                pendingLength += read;

                var messages = new List<RtmpMessage>();
                pendingLength = _decoder.Decode(pending, pendingLength, messages.Add);

                foreach (var message in messages)
                    await HandleMessageAsync(message, lifetime.Token).ConfigureAwait(false);

                await FlushAsync(stream, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "RTMP 连接 {Id} 异常断开", _id);
        }
        finally
        {
            await CleanupAsync(CancellationToken.None).ConfigureAwait(false);
            _client.Dispose();
            _ = _scope.DisposeAsync();
        }
    }

    /// <summary>保活循环：定时发 PingRequest。写失败（对端已消失）就取消连接生命周期。</summary>
    private async Task PingLoopAsync(NetworkStream stream, PeriodicTimer timer, CancellationTokenSource lifetime)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
            {
                var payload = new byte[6];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(payload, 6);   // PingRequest
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(2, 4), Environment.TickCount);

                Send(new RtmpMessage
                {
                    MessageType = RtmpMessageType.UserControl,
                    StreamId = 0,
                    TimestampMs = 0,
                    Payload = payload,
                });

                await FlushAsync(stream, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 宿主持停或连接已结束
        }
        catch (Exception ex)
        {
            _logger.LogDebug("RTMP 连接 {Id} 保活失败：{Message}", _id, ex.Message);
            await lifetime.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        if (_publishing is not null)
        {
            var identity = _publishing;

            _hub.Unpublish(identity);
            _registry.OnStop(identity, ClientId, isPublisher: true);

            await _dispatcher.DispatchEventAsync(
                StreamEventType.Unpublish,
                identity,
                ClientId,
                _remoteAddress,
                "rtmp",
                _connectionProperties,
                cancellationToken).ConfigureAwait(false);
        }

        if (_playing is not null)
        {
            var identity = _playing;

            if (_playingSubscription != Guid.Empty)
                _hub.Unsubscribe(identity, _playingSubscription);

            _registry.OnStop(identity, ClientId, isPublisher: false);

            await _dispatcher.DispatchEventAsync(
                StreamEventType.Stop,
                identity,
                ClientId,
                _remoteAddress,
                "rtmp",
                _connectionProperties,
                cancellationToken).ConfigureAwait(false);
        }

        _publishing = null;
        _playing = null;
        _playingSubscription = Guid.Empty;
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

            case RtmpMessageType.UserControl:
                HandleUserControl(message);
                return;

            case RtmpMessageType.DataAmf0:
                HandleData(message);
                return;
        }
    }

    /// <summary>
    /// 用户控制消息：对端的 PingRequest 必须回 PingResponse，否则对端按超时掐断我们。
    /// </summary>
    private void HandleUserControl(RtmpMessage message)
    {
        if (message.Payload.Length < 2)
            return;

        var eventType = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(message.Payload);

        if (eventType != 6)   // PingRequest
            return;

        var payload = new byte[6];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(payload, 7);   // PingResponse

        // 回显对端的时间戳（规范 6.3.1）
        message.Payload.AsSpan(2, 4).CopyTo(payload.AsSpan(2));

        Send(new RtmpMessage
        {
            MessageType = RtmpMessageType.UserControl,
            StreamId = 0,
            TimestampMs = 0,
            Payload = payload,
        });
    }

    private async Task HandleCommandAsync(RtmpMessage message, CancellationToken cancellationToken)
    {
        var reader = new Amf0Reader(message.Payload);
        var command = reader.ReadValue() as string ?? "";
        // 命令布局统一为：命令名, transactionId, commandObject(通常 null), ...具体参数
        var transactionId = ReadNumber(ref reader);

        // command object：connect 带 app/tcUrl/token 等，publish/play 也可能带
        var commandObject = reader.ReadValue();
        var properties = ReadProperties(commandObject);

        switch (command)
        {
            case "connect":
                // Amf0Reader 是 ref struct：不能作为异步方法参数，先同步取出需要的信息
                HandleConnect(message.StreamId, transactionId, properties);
                break;

            case "releaseStream":
            case "FCPublish":
                SendResult(message.StreamId, transactionId);
                break;

            case "createStream":
                SendResult(message.StreamId, transactionId, 1);
                break;

            case "publish":
                await HandlePublishAsync(
                    ReadPublishName(reader), properties, message.StreamId, cancellationToken).ConfigureAwait(false);
                break;

            case "play":
                await HandlePlayAsync(ReadPlayName(reader), properties, message.StreamId, cancellationToken).ConfigureAwait(false);
                break;

            case "FCUnpublish":
            case "closeStream":
            case "deleteStream":
                await CleanupAsync(cancellationToken).ConfigureAwait(false);
                SendResult(message.StreamId, transactionId);
                break;

            default:
                _logger.LogDebug("RTMP 连接 {Id} 未处理命令 {Command}", _id, command);
                break;
        }
    }

    /// <summary>把 AMF0 命令对象读成字典（取 app/token 等用）。</summary>
    private static Dictionary<string, string> ReadProperties(object? commandObject) =>
        commandObject is Dictionary<string, object?> map
            ? map.Where(pair => pair.Value is not null)
                .ToDictionary(pair => pair.Key, pair => pair.Value!.ToString() ?? "", StringComparer.OrdinalIgnoreCase)
            : [];

    /// <summary>从连接属性/流名里取令牌：URL 查询串（app?token=x）或独立 token 字段都认。</summary>
    private static string ReadToken(IReadOnlyDictionary<string, string> properties, string? streamName)
    {
        if (properties.TryGetValue("token", out var token) && !string.IsNullOrEmpty(token))
            return token;

        foreach (var value in new[] { properties.GetValueOrDefault("app"), streamName })
        {
            if (value is null)
                continue;

            var query = value.Split('?', 2);
            if (query.Length < 2)
                continue;

            foreach (var pair in query[1].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 && parts[0].Equals("token", StringComparison.OrdinalIgnoreCase))
                    return Uri.UnescapeDataString(parts[1]);
            }
        }

        return "";
    }

    private void HandleConnect(int streamId, double transactionId, IReadOnlyDictionary<string, string> properties)
    {
        _connectionProperties = properties;

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

    private async Task HandlePublishAsync(
        string streamName,
        IReadOnlyDictionary<string, string> properties,
        int streamId,
        CancellationToken cancellationToken)
    {
        // 发布名形如 {app}/{name} 或 {app}/{name}?token=x（令牌也可以是 connect 对象里的字段）
        var identity = StreamIdentity.Parse(StripQuery(streamName));
        var parameters = MergeParameters(_connectionProperties, ParseQuery(streamName));

        if (_options.RequirePublishToken)
        {
            var authorized = await _authenticator.AuthorizePublishAsync(
                NewContext(identity, ReadToken(properties, streamName)), cancellationToken).ConfigureAwait(false);

            if (!authorized)
            {
                _logger.LogWarning("RTMP 连接 {Id} 推流 {Stream} 鉴权失败", _id, streamName);
                SendStatus(streamId, "NetStream.Failed", "error", "鉴权失败：无效的推流令牌");

                return;
            }
        }

        _publishing = identity;
        _hub.Publish(_publishing, $"rtmp:{_id}");

        // 媒体面自己维护在线状态与事件：不依赖外部媒体服务器的 webhook
        _registry.OnPublish(identity, ClientId, _remoteAddress, StreamProtocol.Rtmp, parameters);
        await _dispatcher.DispatchEventAsync(
            StreamEventType.Publish,
            identity,
            ClientId,
            _remoteAddress,
            "rtmp",
            parameters,
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("RTMP 连接 {Id} 发布 {Stream}", _id, streamName);
        SendStatus(streamId, "NetStream.Publish.Start", "Publishing");
    }

    /// <summary>从 publish 命令里取出发布名（同步：ref struct 不能进异步方法）。</summary>
    private static string ReadPublishName(Amf0Reader reader)
    {
        // reader 已越过命令名/transactionId/commandObject，正对发布名
        return reader.ReadValue() as string ?? "";
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

    private async Task HandlePlayAsync(
        string streamName,
        IReadOnlyDictionary<string, string> properties,
        int streamId,
        CancellationToken cancellationToken)
    {
        _playing = StreamIdentity.Parse(StripQuery(streamName));
        _playingSubscription = Guid.Empty;

        // StreamBegin 用户控制
        SendUserControl(0, streamId);

        // NetStream.Play.Reset + NetStream.Play.Start
        SendStatus(streamId, "NetStream.Play.Reset", "Playing");
        SendStatus(streamId, "NetStream.Play.Start", "Playing");

        if (_options.RequirePlayToken)
        {
            var authorized = await _authenticator.AuthorizePlayAsync(
                NewContext(_playing, ReadToken(properties, streamName)), cancellationToken).ConfigureAwait(false);

            if (!authorized)
            {
                _logger.LogWarning("RTMP 连接 {Id} 拉流 {Stream} 鉴权失败", _id, streamName);
                SendStatus(streamId, "NetStream.Failed", "error", "鉴权失败：无效的拉流令牌");

                return;
            }
        }

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

            _registry.OnPlay(_playing, ClientId);
            await _dispatcher.DispatchEventAsync(
                StreamEventType.Play,
                _playing,
                ClientId,
                _remoteAddress,
                "rtmp",
                ParseQuery(streamName),
                cancellationToken).ConfigureAwait(false);

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

    private void SendStatus(int streamId, string code, string level, string? description = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new Amf0Writer(buffer);
        writer.WriteString("onStatus");
        writer.WriteNumber(0);
        writer.WriteNull();

        writer.WriteObjectStart();
        writer.WriteProperty("level", level);
        writer.WriteProperty("code", code);
        writer.WriteProperty("description", description ?? code);
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
