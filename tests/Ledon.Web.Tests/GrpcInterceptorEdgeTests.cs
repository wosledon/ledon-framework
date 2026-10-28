using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace Ledon.Web.Tests;

/// <summary>gRPC 拦截器边界：异常映射、未知异常兜底、日志成功/失败。</summary>
public class GrpcInterceptorEdgeTests
{
    [Fact]
    public async Task Known_exception_is_mapped_to_status_code()
    {
        var interceptor = new LedonGrpcErrorInterceptor(
            [new DefaultGrpcExceptionMapper()],
            NullLogger<LedonGrpcErrorInterceptor>.Instance);

        var exception = await Assert.ThrowsAsync<RpcException>(() =>
            interceptor.UnaryServerHandler<object, object>(
                new object(),
                Context().Object,
                (_, _) => throw new ArgumentException("bad")));

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
        Assert.Equal("bad", exception.Status.Detail);
    }

    [Fact]
    public async Task Unknown_exception_is_hidden_behind_generic_status()
    {
        var interceptor = new LedonGrpcErrorInterceptor(
            [new DefaultGrpcExceptionMapper()],
            NullLogger<LedonGrpcErrorInterceptor>.Instance);

        var exception = await Assert.ThrowsAsync<RpcException>(() =>
            interceptor.UnaryServerHandler<object, object>(
                new object(),
                Context().Object,
                (_, _) => throw new NotSupportedException("secret")));

        Assert.Equal(StatusCode.Unknown, exception.StatusCode);
        Assert.DoesNotContain("secret", exception.Status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Successful_call_passes_through_without_exception()
    {
        var interceptor = new LedonGrpcErrorInterceptor(
            [new DefaultGrpcExceptionMapper()],
            NullLogger<LedonGrpcErrorInterceptor>.Instance);

        var response = await interceptor.UnaryServerHandler<object, string>(
            new object(),
            Context().Object,
            (_, _) => Task.FromResult("ok"));

        Assert.Equal("ok", response);
    }

    [Fact]
    public async Task Logging_interceptor_returns_response_and_records_failure()
    {
        var interceptor = new LedonGrpcLoggingInterceptor(NullLogger<LedonGrpcLoggingInterceptor>.Instance);
        var context = Context();

        var response = await interceptor.UnaryServerHandler<object, string>(
            new object(),
            context.Object,
            (_, _) => Task.FromResult("ok"));
        Assert.Equal("ok", response);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            interceptor.UnaryServerHandler<object, string>(
                new object(),
                context.Object,
                (_, _) => throw new InvalidOperationException("boom")));
    }

    private static Mock<ServerCallContext> Context()
    {
        var context = new Mock<ServerCallContext>();
        context.Protected().Setup<string>("MethodCore").Returns("TestService/Call");
        context.Protected().Setup<DateTime>("DeadlineCore").Returns(DateTime.UtcNow.AddSeconds(30));
        context.Protected().Setup<CancellationToken>("CancellationTokenCore").Returns(CancellationToken.None);
        return context;
    }
}
