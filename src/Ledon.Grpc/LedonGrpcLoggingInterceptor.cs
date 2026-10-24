using System.Diagnostics;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 默认 gRPC 日志拦截器（<c>AddLedonGrpc(o =&gt; o.UseLoggingInterceptor)</c>，默认开）：
/// 记录每次调用的方法、耗时与成败——gRPC 调用不只在链路里可见，应用日志同样可观测。
/// </summary>
internal sealed class LedonGrpcLoggingInterceptor(ILogger<LedonGrpcLoggingInterceptor> logger)
    : Interceptor
{
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            var response = await continuation(request, context).ConfigureAwait(false);
            logger.LogInformation("gRPC {Method} 完成（{ElapsedMs}ms）",
                context.Method, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "gRPC {Method} 失败（{ElapsedMs}ms）",
                context.Method, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            var response = await continuation(requestStream, context).ConfigureAwait(false);
            logger.LogInformation("gRPC 客户端流 {Method} 完成（{ElapsedMs}ms）",
                context.Method, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "gRPC 客户端流 {Method} 失败", context.Method);
            throw;
        }
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            await continuation(request, responseStream, context).ConfigureAwait(false);
            logger.LogInformation("gRPC 服务端流 {Method} 完成（{ElapsedMs}ms）",
                context.Method, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "gRPC 服务端流 {Method} 失败", context.Method);
            throw;
        }
    }
    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            await continuation(requestStream, responseStream, context).ConfigureAwait(false);
            logger.LogInformation("gRPC 双流 {Method} 完成（{ElapsedMs}ms）",
                context.Method, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "gRPC 双流 {Method} 失败", context.Method);
            throw;
        }
    }
}
