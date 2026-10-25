using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Ledon.Sip;
using Ledon.Sockets.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ledon.Sip.Client;

/// <summary>
/// 一次对话（RFC 3261 §12）：由 Call-ID + 双方 tag 确定。
/// 对话内的后续请求（ACK/BYE/re-INVITE）必须沿着对话的 Route/Contact 走，
/// 并且 CSeq 只在本方向递增——这些都由 <see cref="SipDialog"/> 记住。
/// </summary>
public sealed class SipDialog
{
    /// <summary>对话标识。</summary>
    public required string CallId { get; init; }

    /// <summary>我们的 tag（From tag）。</summary>
    public required string LocalTag { get; init; }

    /// <summary>对端 tag（To tag）。</summary>
    public required string RemoteTag { get; init; }

    /// <summary>对端目标（Contact URI），对话内请求的 Request-URI。</summary>
    public string? RemoteTarget { get; init; }

    /// <summary>远端 Via 主机（回包路由用）。</summary>
    public string? RemoteHost { get; init; }

    /// <summary>远端端口。</summary>
    public int RemotePort { get; init; } = 5060;

    /// <summary>本方向已用过的最大 CSeq。</summary>
    public uint LocalCSeq { get; set; }

    /// <summary>对方向的 CSeq。</summary>
    public uint RemoteCSeq { get; init; }

    /// <summary>对话是否已确认（收到/发出 2xx）。</summary>
    public bool Confirmed { get; init; }

    /// <summary>对话键：Call-ID + 双方 tag（顺序无关，因为双方算出来的必须一致）。</summary>
    public string Key
    {
        get
        {
            var pair = string.CompareOrdinal(LocalTag, RemoteTag) <= 0
                ? $"{LocalTag},{RemoteTag}"
                : $"{RemoteTag},{LocalTag}";

            return $"{CallId}|{pair}";
        }
    }

    /// <summary>
    /// 在对话内构造请求：Request-URI 走对端 Contact，Route/Via/From/To/Call-ID 全部按对话状态填。
    /// </summary>
    public SipMessage CreateRequest(string method, string userAgent = "Ledon/1.0", string? body = null)
    {
        var request = new SipMessage
        {
            Type = SipMessageType.Request,
            Method = method.ToUpperInvariant(),
            RequestUri = RemoteTarget ?? $"sip:{RemoteHost}:{RemotePort}",
        };

        request.AddHeader("Via", $"SIP/2.0/UDP {RemoteHost}:{RemotePort};branch={SipClient.NewBranch()}");
        request.AddHeader("From", $"<{request.RequestUri}>;tag={LocalTag}");
        request.AddHeader("To", $"<{request.RequestUri}>;tag={RemoteTag}");
        request.AddHeader("Call-ID", CallId);
        request.AddHeader("Max-Forwards", "70");
        request.AddHeader("User-Agent", userAgent);

        LocalCSeq++;
        request.AddHeader("CSeq", $"{LocalCSeq.ToString(CultureInfo.InvariantCulture)} {method.ToUpperInvariant()}");

        return request.WithBody(body);
    }
}

/// <summary>
/// SIP 客户端（事务用户 + 对话管理）：发请求 → 等最终响应。
/// <list type="bullet">
///   <item>基于 <see cref="Ledon.Sockets.Client"/> 的 socket 库（UDP/TCP，socket 层不重复造）；</item>
///   <item>自动生成 Via/branch/Call-ID/From tag/CSeq/Contact：调用方只给方法和 URI；</item>
///   <item>事务匹配：按 <c>Via branch</c> 关联响应（RFC 3261 的规定做法）；</item>
///   <item>临时响应（1xx）不完成事务，只回调观察者；</item>
///   <item>UDP 上按 <see cref="SipClientOptions.RetryInterval"/> 指数重传，超
///         <see cref="SipClientOptions.TransactionTimeout"/> 抛 <see cref="TimeoutException"/>；</item>
///   <item>**非 2xx 的 INVITE 自动补 ACK**（规范 17.1.1.2 强制要求，漏了会被对端判为非法）；</item>
///   <item>对话跟踪：BYE/re-INVITE 自动带上正确的 Request-URI 与 CSeq。</item>
/// </list>
/// </summary>
public sealed class SipClient : IAsyncDisposable
{
    private readonly SipClientOptions _options;
    private readonly ILogger<SipClient> _logger;
    private readonly IProtocolClient _transport;
    private readonly ConcurrentDictionary<string, Transaction> _transactions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SipDialog> _dialogs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _cseqGate = new(1, 1);

    /// <summary>每个 Call-ID 的本方向 CSeq 序号。</summary>
    private readonly ConcurrentDictionary<string, uint> _localCSeq = new(StringComparer.Ordinal);

    public SipClient(SipClientOptions options, ILogger<SipClient>? logger = null)
    {
        _options = options;
        _logger = logger ?? NullLogger<SipClient>.Instance;

        var clientOptions = new ProtocolClientOptions
        {
            Host = options.Host,
            Port = options.Port,
            ConnectTimeout = options.ConnectTimeout,
            LocalPort = options.LocalPort,
            LocalBindAddress = options.LocalBindAddress,
        };

        // SIP 数据报即消息：不需要包编解码，直接发序列化后的字节。
        // TCP 上用 Content-Length 切帧（同一个响应泵接口，传输细节被 socket 库吃掉）
        _transport = options.Transport == SipTransport.Tcp
            ? new TcpProtocolClient(clientOptions, new SipTcpFrameCodec(), new ResponsePump(this), NullLogger<TcpProtocolClient>.Instance)
            : new UdpProtocolClient(clientOptions, new ResponsePump(this));
    }

    /// <summary>是否已连接。</summary>
    public bool IsConnected => _transport.IsConnected;

    /// <summary>本地绑定端口（写进 Via/Contact 的那个）。</summary>
    public int LocalPort => _transport is UdpProtocolClient udp ? udp.LocalPort : _options.LocalPort;

    /// <summary>当前对话（运维/排查用）。</summary>
    public IReadOnlyCollection<SipDialog> Dialogs => _dialogs.Values.ToArray();

    /// <summary>连接（幂等）。</summary>
    public Task ConnectAsync(CancellationToken cancellationToken = default) =>
        _transport.ConnectAsync(cancellationToken);

    // ---------- 高层 API ----------

    /// <summary>OPTIONS（保活探测/能力查询）。</summary>
    public Task<SipMessage> OptionsAsync(string requestUri, CancellationToken cancellationToken = default) =>
        SendAsync("OPTIONS", requestUri, null, cancellationToken: cancellationToken);

    /// <summary>REGISTER（注册）。</summary>
    public Task<SipMessage> RegisterAsync(
        string requestUri, int expiresSeconds = 3600, CancellationToken cancellationToken = default)
    {
        var request = Build("REGISTER", requestUri);
        request.AddHeader("Contact", $"<sip:{LocalContactHost()}:{LocalPort}>;expires={expiresSeconds}");
        request.AddHeader("Expires", expiresSeconds.ToString(CultureInfo.InvariantCulture));

        return SendAsync(request, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// INVITE：返回 (对话, 最终响应)。2xx 会建立对话；非 2xx 自动补 ACK 后返回。
    /// </summary>
    public async Task<(SipDialog? Dialog, SipMessage Response)> InviteAsync(
        string requestUri,
        string? sdp = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var request = Build("INVITE", requestUri, sdp);
        var response = await SendAsync(request, timeout, cancellationToken).ConfigureAwait(false);

        var dialog = response.IsFinalResponse && response.StatusCode is >= 200 and < 300
            ? EstablishDialog(request, response)
            : null;

        return (dialog, response);
    }

    /// <summary>对话内 BYE（结束通话）。</summary>
    public Task<SipMessage> ByeAsync(SipDialog dialog, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        var request = dialog.CreateRequest("BYE", _options.UserAgent);

        return SendAsync(request, cancellationToken: cancellationToken);
    }

    /// <summary>CANCEL：取消一个尚未收到最终响应的 INVITE（RFC 3261 §9.1）。</summary>
    public Task CancelAsync(SipMessage invite, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invite);

        var cancel = new SipMessage
        {
            Type = SipMessageType.Request,
            Method = "CANCEL",
            RequestUri = invite.RequestUri,
        };

        // CANCEL 必须逐字复制原请求的 Via/From/To/Call-ID（不含 Contact），CSeq 方法换成 CANCEL
        foreach (var name in new[] { "Via", "From", "To", "Call-ID" })
            foreach (var value in invite.HeaderValues(name))
                cancel.AddHeader(name, value);

        var cseq = invite.CSeqValue;
        cancel.AddHeader("CSeq", $"{cseq?.Number ?? 1} CANCEL");
        cancel.AddHeader("Max-Forwards", "70");
        cancel.AddHeader("Content-Length", "0");

        return SendAsync(cancel, cancellationToken: cancellationToken);
    }

    /// <summary>ACK：确认最终响应（2xx 由对话继续，非 2xx 结束事务）。</summary>
    public Task AckAsync(SipMessage request, SipMessage response, CancellationToken cancellationToken = default)
    {
        var ack = new SipMessage
        {
            Type = SipMessageType.Request,
            Method = "ACK",
            RequestUri = response.ContactAddress?.Uri ?? request.RequestUri,
        };

        // ACK 的 CSeq 必须与被确认的请求相同（规范 17.1.1.3）
        var cseq = request.CSeqValue;
        ack.AddHeader("CSeq", $"{cseq?.Number ?? 1} ACK");

        foreach (var name in new[] { "Via", "From", "To", "Call-ID" })
            foreach (var value in request.HeaderValues(name))
                ack.AddHeader(name, value);

        ack.AddHeader("Max-Forwards", "70");
        ack.AddHeader("Content-Length", "0");

        return SendRawAsync(ack, cancellationToken);
    }

    // ---------- 底层 API ----------

    /// <summary>发送请求并等待最终响应（2xx-6xx）。临时响应（1xx）不完成事务。</summary>
    public async Task<SipMessage> SendAsync(
        SipMessage request,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Prepare(request);

        // 事务键 = branch + CSeq 方法：CANCEL 与它取消的 INVITE 共用同一个 branch，
        // 只按 branch 建键会把两者混成一个事务（规范 9.1/17.1.1.2）
        var branch = request.TopVia?.Branch ?? throw new InvalidOperationException("请求缺少 Via branch");
        var key = $"{branch}|{request.CSeqValue?.Method ?? request.Method}";
        var transaction = new Transaction(key);
        _transactions[key] = transaction;

        try
        {
            await SendRawAsync(request, cancellationToken).ConfigureAwait(false);

            using var retryCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var limit = timeout ?? _options.TransactionTimeout;
            retryCts.CancelAfter(limit);

            // UDP 重传：间隔翻倍直到上限或事务完成（Timer A 的指数规律）
            var interval = _options.RetryInterval;
            var attempts = 0;

            while (!transaction.Completion.Task.IsCompleted && !retryCts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, retryCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (transaction.Completion.Task.IsCompleted)
                    break;

                if (++attempts > _options.MaxRetries)
                    break;

                _logger.LogDebug("SIP 事务 {Branch} 重传（第 {Attempt} 次）", branch, attempts);

                await SendRawAsync(request, cancellationToken).ConfigureAwait(false);
                interval = TimeSpan.FromTicks(interval.Ticks * 2);
            }

            var response = await transaction.Completion.Task.WaitAsync(retryCts.Token).ConfigureAwait(false);

            // 非 2xx 的 INVITE 必须回 ACK，否则对端事务不结束（规范 17.1.1.2）
            if (request.Method == "INVITE" && response.IsFinalResponse && response.StatusCode is < 200 or >= 300)
                await AckAsync(request, response, cancellationToken).ConfigureAwait(false);

            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"SIP 事务 {branch} 在 {timeout ?? _options.TransactionTimeout} 内未完成");
        }
        finally
        {
            _transactions.TryRemove(key, out _);
        }
    }

    /// <summary>便捷方法：方法 + URI → 请求。</summary>
    public Task<SipMessage> SendAsync(
        string method,
        string requestUri,
        string? body = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        SendAsync(Build(method, requestUri, body), timeout, cancellationToken);

    /// <summary>
    /// 发送请求并等待最终响应。<c>SendAsync</c> 的别名（保留早期 API 命名）。
    /// </summary>
    public Task<SipMessage> SendRequestAsync(
        SipMessage request,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        SendAsync(request, timeout, cancellationToken);

    /// <summary>直接发送（不建事务，用于 ACK/CANCEL 这类"发了就不管"的消息）。</summary>
    public Task SendRawAsync(SipMessage message, CancellationToken cancellationToken = default) =>
        _transport.SendAsync(message.ToBytes(), cancellationToken);

    // ---------- 构造 ----------

    /// <summary>构造一个带全必备头的请求（调用方只管方法和 URI）。</summary>
    public SipMessage Build(string method, string requestUri, string? body = null)
    {
        var message = new SipMessage
        {
            Type = SipMessageType.Request,
            Method = method.ToUpperInvariant(),
            RequestUri = requestUri,
        };

        var from = new SipNameAddr
        {
            Uri = $"sip:{_options.UserAgent}@{LocalContactHost()}",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["tag"] = NewTag(),
            },
        };

        message.AddHeader("Via", $"SIP/2.0/{TransportName()} {LocalContactHost()}:{LocalPort};branch={NewBranch()}");
        message.AddHeader("From", $"{from.Uri};tag={from.Tag}");
        message.AddHeader("To", requestUri);
        message.AddHeader("Call-ID", Guid.NewGuid().ToString("N"));
        message.AddHeader("CSeq", "1 " + method.ToUpperInvariant());
        message.AddHeader("Max-Forwards", "70");
        message.AddHeader("Contact", $"<sip:{LocalContactHost()}:{LocalPort}>");
        message.AddHeader("User-Agent", _options.UserAgent);

        return message.WithBody(body);
    }

    /// <summary>补齐缺失的事务头（调用方自己构造的消息也能用）。</summary>
    public void Prepare(SipMessage request)
    {
        if (!request.HasHeader("Via"))
            request.AddHeader("Via", $"SIP/2.0/{TransportName()} {LocalContactHost()}:{LocalPort};branch={NewBranch()}");

        if (!request.HasHeader("From") || !request.From!.Contains("tag=", StringComparison.OrdinalIgnoreCase))
            request.WithHeader("From", $"{request.From ?? $"sip:anon@{LocalContactHost()}"};tag={NewTag()}");

        if (!request.HasHeader("To"))
            request.AddHeader("To", request.RequestUri ?? "");

        if (!request.HasHeader("Call-ID"))
            request.AddHeader("Call-ID", Guid.NewGuid().ToString("N"));

        if (!request.HasHeader("CSeq"))
            request.WithHeader("CSeq", $"1 {request.Method}");

        if (!request.HasHeader("Max-Forwards"))
            request.AddHeader("Max-Forwards", "70");

        if (!request.HasHeader("Contact") && request.Method is "INVITE" or "REGISTER" or "OPTIONS")
            request.AddHeader("Contact", $"<sip:{LocalContactHost()}:{LocalPort}>");

        if (!request.HasHeader("User-Agent"))
            request.AddHeader("User-Agent", _options.UserAgent);
    }

    private SipDialog? EstablishDialog(SipMessage request, SipMessage response)
    {
        var from = request.FromAddress;
        var to = response.ToAddress;

        if (from?.Tag is null || to?.Tag is null)
            return null;   // 没有双方 tag 就不成对话

        var dialog = new SipDialog
        {
            CallId = request.CallId ?? "",
            LocalTag = from.Tag,
            RemoteTag = to.Tag,
            RemoteTarget = response.ContactAddress?.Uri,
            RemoteHost = response.TopVia?.Received ?? response.TopVia?.Host,
            RemotePort = response.TopVia?.Port is > 0 ? response.TopVia.Port : _options.Port,
            LocalCSeq = request.CSeqValue?.Number ?? 1,
            RemoteCSeq = response.CSeqValue?.Number ?? 0,
            Confirmed = true,
        };

        _dialogs[dialog.Key] = dialog;

        return dialog;
    }

    private string LocalContactHost() =>
        _options.ContactHost ?? _options.LocalBindAddress ?? _options.Host;

    private string TransportName() => _options.Transport == SipTransport.Tcp ? "TCP" : "UDP";

    /// <summary>branch：规范要求以 <c>z9hG4bK-</c> 开头且全局唯一。</summary>
    public static string NewBranch() => $"z9hG4bK-{Guid.NewGuid():n}";

    /// <summary>tag：时间 + 随机，够唯一又不泄露信息。</summary>
    public static string NewTag() => $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}{Random.Shared.Next():x8}";

    // ---------- 响应处理 ----------

    private Task OnResponseAsync(SipMessage response)
    {
        // 响应只带 Via branch，但它回答哪个请求要看 CSeq 方法
        var branch = response.TopVia?.Branch;
        var method = response.CSeqValue?.Method;

        if (branch is not null && method is not null && _transactions.TryGetValue($"{branch}|{method}", out var transaction))
        {
            if (response.IsFinalResponse)
                transaction.Complete(response);
            else
                _logger.LogDebug("SIP 事务 {Branch}/{Method} 收到临时响应 {Code}", branch, method, response.StatusCode);
        }
        else
        {
            _logger.LogDebug("SIP 收到无所属事务的响应 {Code}（可能已超时）", response.StatusCode);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var transaction in _transactions.Values)
            transaction.Abandon();

        _transactions.Clear();

        await _transport.DisposeAsync().ConfigureAwait(false);
        _cseqGate.Dispose();
    }

    private sealed class Transaction(string branch)
    {
        private readonly TaskCompletionSource<SipMessage> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<SipMessage> Completion => _completion;

        public void Complete(SipMessage response) => _completion.TrySetResult(response);

        public void Abandon() => _completion.TrySetCanceled();

        public override string ToString() => branch;
    }

    /// <summary>把 socket 客户端收到的帧解析成 SIP 消息并交给客户端。</summary>
    private sealed class ResponsePump(SipClient owner) : ClientHandlerBase
    {
        public override async Task OnFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
        {
            var message = SipParser.Parse(frame.Span);

            if (message.Type == SipMessageType.Response)
                await owner.OnResponseAsync(message).ConfigureAwait(false);
        }
    }
}
