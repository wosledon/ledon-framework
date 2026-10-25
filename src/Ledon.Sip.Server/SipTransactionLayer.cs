using System.Collections.Concurrent;
using System.Net;
using Ledon.Sip;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ledon.Sip.Server;

/// <summary>事务层选项（RFC 3261 定时器的可调版本）。</summary>
public sealed class SipTransactionOptions
{
    /// <summary>T1（RTT 估计起点），默认 500 毫秒。</summary>
    public TimeSpan T1 { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Timer F/H（等 ACK / 重传上限），默认 32 秒（64×T1）。</summary>
    public TimeSpan TimerFH { get; set; } = TimeSpan.FromSeconds(32);

    /// <summary>Timer I（INVITE 事务存留），默认 5 秒（T4）。</summary>
    public TimeSpan TimerI { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>最大并发事务数，默认 10000（超出时丢弃新事务，防伪造 branch 打爆内存）。</summary>
    public int MaxTransactions { get; set; } = 10000;
}

/// <summary>
/// SIP 服务端事务层（RFC 3261 §17）：让"丢了响应"不再等于"通话失败"。
/// <list type="bullet">
///   <item>**响应重传**：UDP 上按 T1 指数间隔重发最终响应，直到收到 ACK（Timer G/H）
///         或超时——客户端重发 INVITE 时也必须能拿到同一份响应；</item>
///   <item>**ACK 处理**：ACK 结束对应事务并停掉重传；</item>
///   <item>**重复请求**：同 branch 的请求重发上次的响应，不重复执行业务；</item>
///   <item>**可靠传输**（TCP/TLS）不重传，只等过期；</item>
///   <item>事务数有上限：伪造 branch 的洪水不会无限占用内存。</item>
/// </list>
/// </summary>
public sealed class SipTransactionLayer : IHostedService
{
    private readonly ConcurrentDictionary<string, ServerTransaction> _transactions = new(StringComparer.Ordinal);
    private readonly SipTransactionOptions _options;
    private readonly ILogger<SipTransactionLayer> _logger;

    public SipTransactionLayer(SipTransactionOptions options, ILogger<SipTransactionLayer> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("SIP 事务层已启动（上限 {Max} 个并发事务）", _options.MaxTransactions);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Clear();

        return Task.CompletedTask;
    }

    /// <summary>当前事务数（运维）。</summary>
    public int TransactionCount => _transactions.Count;

    /// <summary>累计因重复请求而重发的响应数。</summary>
    public long RetransmittedResponses { get; private set; }

    /// <summary>累计因事务已结束/超限而丢弃的请求数。</summary>
    public long DroppedRequests { get; private set; }

    /// <summary>
    /// 处理一条收到的请求：建事务 → 调 UAS → 发响应 → 按需重传。
    /// </summary>
    /// <param name="request">解析后的请求。</param>
    /// <param name="send">响应发送通道（重传也走它：响应必须回到请求来的地方）。</param>
    /// <param name="uas">业务处理器（返回要发的响应；null = 不应答）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task HandleAsync(
        SipMessage request,
        Func<byte[], CancellationToken, Task> send,
        Func<SipMessage, CancellationToken, Task<SipMessage?>> uas,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(uas);

        var branch = request.TopVia?.Branch
            ?? throw new SipParseException("请求缺少 Via 头的 branch 参数（无法建事务）");

        // 事务键 = branch + 方法：CANCEL 与它取消的 INVITE 共用同一个 branch
        // （规范 9.1 就是这么规定的），只按 branch 建键会把两者混成一个事务
        var key = $"{branch}|{request.Method}";
        var reliable = request.TopVia?.IsReliable ?? false;

        // ACK：结束对应的 INVITE 事务（同时停掉响应重传）。
        // 规范上非 2xx 的 ACK 由事务层吸收，但业务侧需要知道"通话结束了"，
        // 所以首次 ACK 也通知 UAS（重传的 ACK 不再通知）。
        if (request.Method == "ACK")
        {
            if (_transactions.TryRemove($"{branch}|INVITE", out var invited))
            {
                invited.OnAck();
                _logger.LogDebug("SIP 事务 {Key} 收到 ACK，结束", key);

                try
                {
                    await uas(request, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SIP ACK 通知业务失败（{Key}）", key);
                }
            }

            return;
        }

        // 重复请求（客户端没收到响应所以重发）：原样重发上次的响应，不重复执行业务。
        // 并发到达的重复请求也走这里：TryAdd 失败的等赢家把响应做出来再原样发一份
        if (_transactions.TryGetValue(key, out var existing))
        {
            await ResendAsync(existing, send, cancellationToken).ConfigureAwait(false);

            return;
        }

        if (_transactions.Count >= _options.MaxTransactions)
        {
            DroppedRequests++;
            _logger.LogWarning("SIP 事务数已达上限 {Max}，丢弃 {Method}", _options.MaxTransactions, request.Method);

            return;
        }

        var transaction = new ServerTransaction(key, reliable, _options, _logger);

        // 原子占位：同一 branch 的重复请求并发到达时，只有一个能建事务、
        // 只有一个会执行业务（否则一次 INVITE 会被处理两遍）
        if (!_transactions.TryAdd(key, transaction))
        {
            await ResendAsync(_transactions[key], send, cancellationToken).ConfigureAwait(false);

            return;
        }

        SipMessage? response;

        try
        {
            response = await uas(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SIP 业务处理 {Method} 失败", request.Method);
            response = SipResponder.Response(request, 500, "Server Internal Error");
        }

        if (response is null)
        {
            _transactions.TryRemove(key, out _);
            return;
        }

        transaction.SetResponse(response);
        transaction.BindSender(send);

        await send(response.ToBytes(), cancellationToken).ConfigureAwait(false);

        if (!reliable)
        {
            // UDP：INVITE 与非 INVITE 都重传到 Timer F/H；收到 ACK 立即停
            _ = Task.Run(() => transaction.RetransmitLoopAsync(_options.TimerFH), CancellationToken.None);
        }
        else if (request.Method == "INVITE")
        {
            // 可靠传输：不重传，只等 ACK；超时事务自然过期
            _ = Task.Run(() => transaction.ExpireAsync(_options.TimerFH), CancellationToken.None);
        }
        else
        {
            _transactions.TryRemove(key, out _);
        }
    }

    /// <summary>清理全部事务（宿主持停用）。</summary>
    public void Clear()
    {
        foreach (var transaction in _transactions.Values)
            transaction.Cancel();

        _transactions.Clear();
    }

    /// <summary>
    /// 原样重发某个事务上次的响应。响应还没做出来时先等它就绪
    /// （并发重复请求的场景），等不到就丢掉——客户端会再重传。
    /// </summary>
    private async Task ResendAsync(
        ServerTransaction transaction,
        Func<byte[], CancellationToken, Task> send,
        CancellationToken cancellationToken)
    {
        if (transaction.LastResponse is null)
        {
            // Task.WaitAsync 只保证"等到或抛异常"，这里要区分"等到了"和"超时了"
            var finished = await Task
                .WhenAny(transaction.ResponseReady, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken))
                .ConfigureAwait(false);

            if (finished != transaction.ResponseReady)
                return;   // 赢家还没做完：客户端会再重传
        }

        if (transaction.LastResponse is not { } previous)
            return;

        RetransmittedResponses++;
        await send(previous.ToBytes(), cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("SIP 事务重发上次的响应（对端重传了请求）");
    }

    private sealed class ServerTransaction : IDisposable
    {
        private readonly string _branch;
        private readonly bool _reliable;
        private readonly SipTransactionOptions _options;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly TaskCompletionSource _responseReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ServerTransaction(
            string branch,
            bool reliable,
            SipTransactionOptions options,
            ILogger logger)
        {
            _branch = branch;
            _reliable = reliable;
            _options = options;
            _logger = logger;
        }

        /// <summary>最近一次发出的响应（重传/重复请求用）。</summary>
        public SipMessage? LastResponse { get; private set; }

        /// <summary>响应就绪信号（并发重复请求等它，然后原样转发）。</summary>
        public Task ResponseReady => _responseReady.Task;

        /// <summary>记录响应并唤醒等待者。</summary>
        public void SetResponse(SipMessage response)
        {
            LastResponse = response;
            _responseReady.TrySetResult();
        }

        /// <summary>发送钩子（建事务后由事务层绑定）。</summary>
        public Func<byte[], CancellationToken, Task>? Send { get; private set; }

        public void BindSender(Func<byte[], CancellationToken, Task> send) => Send = send;

        /// <summary>收到 ACK：停掉重传。</summary>
        public void OnAck()
        {
            try
            {
                _lifetime.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已结束
            }
        }

        /// <summary>重传循环：T1、2×T1、4×T1……直到上限或 ACK。</summary>
        public async Task RetransmitLoopAsync(TimeSpan limit)
        {
            if (Send is null)
                return;

            var interval = _options.T1;
            var deadline = DateTime.UtcNow + limit;

            try
            {
                while (!_lifetime.IsCancellationRequested && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(interval, _lifetime.Token).ConfigureAwait(false);

                    if (_lifetime.IsCancellationRequested || DateTime.UtcNow >= deadline)
                        break;

                    var payload = LastResponse?.ToBytes();
                    if (payload is null)
                        break;

                    await _writeGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);

                    try
                    {
                        await Send(payload, _lifetime.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        _writeGate.Release();
                    }

                    // 指数退避，封顶 Timer I（避免 32 秒里发太密）
                    interval = TimeSpan.FromTicks(Math.Min(interval.Ticks * 2, _options.TimerI.Ticks));
                }
            }
            catch (OperationCanceledException)
            {
                // 收到 ACK 或宿主持停：正常退出
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "SIP 事务 {Branch} 重传失败", _branch);
            }
        }

        /// <summary>只等过期（可靠传输上没有重传）。</summary>
        public async Task ExpireAsync(TimeSpan limit)
        {
            try
            {
                await Task.Delay(limit, _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 收到 ACK
            }
        }

        public void Cancel()
        {
            try
            {
                _lifetime.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已取消
            }
        }

        public void Dispose()
        {
            _lifetime.Dispose();
            _writeGate.Dispose();
        }
    }
}
