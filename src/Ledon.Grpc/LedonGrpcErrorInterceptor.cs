using System.Diagnostics;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// gRPC 异常 → 状态码映射：把业务异常转成客户端能处理的 <see cref="RpcException"/>，
/// 而不是一律变成 <see cref="StatusCode.Unknown"/> + 堆栈（那等于把内部结构送给调用方）。
/// <para>
/// 拦截器顺序：异常映射在**日志之前**（外层），所以日志记的是最终状态码而不是 Unknown。
/// 自定义映射实现 <see cref="IGrpcExceptionMapper"/> 并注册进容器即可，无需改框架。
/// </para>
/// </summary>
public interface IGrpcExceptionMapper
{
    /// <summary>尝试映射；返回 false 表示"不归我管"，交给下一个映射或默认规则。</summary>
    bool TryMap(Exception exception, out StatusCode statusCode, out string detail);
}

/// <summary>默认映射表（可被自定义映射覆盖顺序在前）。</summary>
public sealed class DefaultGrpcExceptionMapper : IGrpcExceptionMapper
{
    public bool TryMap(Exception exception, out StatusCode statusCode, out string detail)
    {
        switch (exception)
        {
            case ArgumentException argument:
                statusCode = StatusCode.InvalidArgument;
                detail = argument.Message;
                return true;

            case KeyNotFoundException:
                statusCode = StatusCode.NotFound;
                detail = exception.Message;
                return true;

            case UnauthorizedAccessException:
                statusCode = StatusCode.PermissionDenied;
                detail = exception.Message;
                return true;

            case OperationCanceledException:
                statusCode = StatusCode.Cancelled;
                detail = exception.Message;
                return true;

            case TimeoutException:
                statusCode = StatusCode.DeadlineExceeded;
                detail = exception.Message;
                return true;

            case InvalidOperationException:
                statusCode = StatusCode.FailedPrecondition;
                detail = exception.Message;
                return true;

            default:
                statusCode = StatusCode.Unknown;
                detail = "服务器内部错误";
                return false;   // 未知异常交给默认兜底（不泄露消息）
        }
    }
}

/// <summary>异常映射 + deadline 强制的服务端拦截器。</summary>
public sealed class LedonGrpcErrorInterceptor(
    IEnumerable<IGrpcExceptionMapper> mappers,
    ILogger<LedonGrpcErrorInterceptor> logger) : Interceptor
{
    private readonly IReadOnlyList<IGrpcExceptionMapper> _mappers = mappers.ToArray();

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        using var deadline = EnforceDeadline(context);

        // 把兜底 deadline 与调用方令牌合成一个：处理器无论观察哪个都能及时退出
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken, deadline.Token);

        var scoped = new DeadlineServerCallContext(context, linked.Token);

        try
        {
            return await continuation(request, scoped).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            throw ToRpcException(ex, context);
        }
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        using var deadline = EnforceDeadline(context);

        try
        {
            return await continuation(requestStream, context).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            throw ToRpcException(ex, context);
        }
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        using var deadline = EnforceDeadline(context);

        try
        {
            await continuation(request, responseStream, context).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            throw ToRpcException(ex, context);
        }
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        using var deadline = EnforceDeadline(context);

        try
        {
            await continuation(requestStream, responseStream, context).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            throw ToRpcException(ex, context);
        }
    }

    /// <summary>
    /// deadline 强制：客户端没传 deadline 时用 <see cref="LedonGrpcOptions.DefaultDeadline"/>
    /// 兜底。返回的 CTS 在超时时取消，处理器把它传给自己的异步调用即可真正生效；
    /// 即使处理器忽略它，gRPC 也会在写响应时发现调用已取消而中止。
    /// </summary>
    private static CancellationTokenSource EnforceDeadline(ServerCallContext context)
    {
        var limit = context.Deadline;

        if (limit <= DateTime.UtcNow)
            limit = DateTime.UtcNow.Add(LedonGrpcOptions.DefaultDeadline);

        var cts = new CancellationTokenSource(limit - DateTime.UtcNow);

        // 客户端提前取消/到 deadline 时，级联取消我们造的 CTS
        context.CancellationToken.Register(static state => ((CancellationTokenSource)state!).Cancel(), cts);

        return cts;
    }

    private RpcException ToRpcException(Exception exception, ServerCallContext context)
    {
        foreach (var mapper in _mappers)
        {
            if (mapper.TryMap(exception, out var status, out var detail))
            {
                logger.LogInformation("gRPC {Method} 业务异常 {Type} → {Status}",
                    context.Method, exception.GetType().Name, status);

                return new RpcException(new Status(status, detail));
            }
        }

        // 未映射：Unknown + 通用文案，真实原因只进日志
        logger.LogError(exception, "gRPC {Method} 未处理异常", context.Method);

        return new RpcException(new Status(StatusCode.Unknown, "服务器内部错误"));
    }
}

/// <summary>
/// 包装 <see cref="ServerCallContext"/> 以替换取消令牌（兜底 deadline 生效的唯一途径）。
/// 只覆盖取消令牌，其余成员原样转发——避免为整个上下文造替身。
/// </summary>
file sealed class DeadlineServerCallContext(ServerCallContext inner, CancellationToken token)
    : ServerCallContext
{
    protected override string MethodCore => inner.Method;
    protected override string HostCore => inner.Host;
    protected override string PeerCore => inner.Peer;
    protected override DateTime DeadlineCore => inner.Deadline;
    protected override Metadata RequestHeadersCore => inner.RequestHeaders;
    protected override CancellationToken CancellationTokenCore => token;
    protected override Metadata ResponseTrailersCore => inner.ResponseTrailers;
    protected override Status StatusCore { get => inner.Status; set => inner.Status = value; }
    protected override WriteOptions? WriteOptionsCore { get => inner.WriteOptions; set => inner.WriteOptions = value; }
    protected override AuthContext AuthContextCore => inner.AuthContext;
    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
        inner.CreatePropagationToken(options);
    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) =>
        inner.WriteResponseHeadersAsync(responseHeaders);
}
