using System.Collections.Concurrent;
using Ledon.Sockets.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ledon.Sip.Client;

/// <summary>
/// SIP 客户端（事务用户 + 事务状态管理）：发请求 → 等最终响应。
/// <list type="bullet">
///   <item>基于 <see cref="Ledon.Sockets.Client"/> 的 UDP 客户端（socket 层不重复造）；</item>
///   <item>事务匹配：按 <c>Via branch</c> 关联响应（RFC 3261 的规定做法）；</item>
///   <item>临时响应（1xx）不完成事务，只回调观察者；</item>
///   <item>UDP 上按 <see cref="SipClientOptions.RetryInterval"/> 重传，超
///         <see cref="SipClientOptions.TransactionTimeout"/> 抛 <see cref="TimeoutException"/>。</item>
/// </list>
/// </summary>
public sealed class SipClient : IAsyncDisposable
{
    private readonly SipClientOptions _options;
    private readonly ILogger<SipClient> _logger;
    private readonly UdpProtocolClient _transport;
    private readonly ConcurrentDictionary<string, Transaction> _transactions = new(StringComparer.Ordinal);

    public SipClient(SipClientOptions options, ILogger<SipClient>? logger = null)
    {
        _options = options;
        _logger = logger ?? NullLogger<SipClient>.Instance;

        // SIP 数据报即消息：不需要包编解码，直接发序列化后的字节
        _transport = new UdpProtocolClient(
            new ProtocolClientOptions { Host = options.Host, Port = options.Port },
            new ResponsePump(this));
    }

    /// <summary>是否已连接。</summary>
    public bool IsConnected => _transport.IsConnected;

    /// <summary>连接（幂等）。</summary>
    public Task ConnectAsync(CancellationToken cancellationToken = default) =>
        _transport.ConnectAsync(cancellationToken);

    /// <summary>
    /// 发送请求并等待最终响应（2xx-6xx）。临时响应（1xx）不完成事务。
    /// </summary>
    public async Task<SipMessage> SendRequestAsync(
        SipMessage request,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var branch = Branch(request);
        var transaction = new Transaction(branch);
        _transactions[branch] = transaction;

        try
        {
            await _transport.SendAsync(request.ToBytes(), cancellationToken).ConfigureAwait(false);

            using var retryCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var limit = timeout ?? _options.TransactionTimeout;
            retryCts.CancelAfter(limit);

            // UDP 重传：间隔翻倍直到上限或事务完成
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

                await _transport.SendAsync(request.ToBytes(), cancellationToken).ConfigureAwait(false);
                interval = TimeSpan.FromTicks(interval.Ticks * 2);
            }

            return await transaction.Completion.Task.WaitAsync(retryCts.Token).ConfigureAwait(false);
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
            _transactions.TryRemove(branch, out _);
        }
    }

    private static string Branch(SipMessage request)
    {
        var via = request.Via ?? "";

        const string marker = "branch=";
        var index = via.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            throw new InvalidOperationException("请求缺少 Via 头的 branch 参数（事务无法匹配响应）");

        var value = via[(index + marker.Length)..];
        var end = value.IndexOf(';');
        if (end >= 0)
            value = value[..end];

        return value;
    }

    private Task OnResponseAsync(SipMessage response)
    {
        var branch = Branch(response);

        if (_transactions.TryGetValue(branch, out var transaction))
        {
            if (IsFinal(response.StatusCode))
                transaction.Complete(response);
            else
                _logger.LogDebug("SIP 事务 {Branch} 收到临时响应 {Code}", branch, response.StatusCode);
        }

        return Task.CompletedTask;
    }

    private static bool IsFinal(int statusCode) => statusCode is >= 200 and <= 699;

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _transport.DisposeAsync().ConfigureAwait(false);

    private sealed class Transaction(string branch)
    {
        private readonly TaskCompletionSource<SipMessage> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<SipMessage> Completion => _completion;

        public void Complete(SipMessage response) => _completion.TrySetResult(response);

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

