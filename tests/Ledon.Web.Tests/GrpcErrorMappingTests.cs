using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>
/// gRPC 异常 → 状态码映射：业务异常不该一律变成 Unknown + 堆栈。
/// 直接测映射器（不启动服务端）：映射表是纯函数，行为稳定。
/// </summary>
public class GrpcErrorMappingTests
{
    [Theory]
    [InlineData(typeof(ArgumentException), StatusCode.InvalidArgument)]
    [InlineData(typeof(KeyNotFoundException), StatusCode.NotFound)]
    [InlineData(typeof(UnauthorizedAccessException), StatusCode.PermissionDenied)]
    [InlineData(typeof(OperationCanceledException), StatusCode.Cancelled)]
    [InlineData(typeof(TimeoutException), StatusCode.DeadlineExceeded)]
    [InlineData(typeof(InvalidOperationException), StatusCode.FailedPrecondition)]
    public void Known_exceptions_map_to_meaningful_status_codes(Type exceptionType, StatusCode expected)
    {
        var mapper = new DefaultGrpcExceptionMapper();
        var exception = (Exception)Activator.CreateInstance(exceptionType, "boom")!;

        Assert.True(mapper.TryMap(exception, out var status, out _));
        Assert.Equal(expected, status);
    }

    [Fact]
    public void Unknown_exceptions_do_not_leak_their_message()
    {
        var mapper = new DefaultGrpcExceptionMapper();

        // 未映射的异常：返回 false（让兜底给 Unknown + 通用文案），而不是把异常消息交给客户端
        var mapped = mapper.TryMap(new NotSupportedException("connection string=secret;password=xxx"), out _, out var detail);

        Assert.False(mapped);
        Assert.DoesNotContain("secret", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Custom_mappers_take_precedence_over_the_default_table()
    {
        // 业务包可实现 IGrpcExceptionMapper 认领自己的异常，注册后优先于默认表
        var custom = new DomainExceptionMapper();
        Assert.True(custom.TryMap(new InsufficientStockException("sku-1"), out var status, out var detail));
        Assert.Equal(StatusCode.ResourceExhausted, status);
        Assert.Contains("sku-1", detail);
    }

    [Fact]
    public void Error_interceptor_is_registered_by_default()
    {
        var services = new ServiceCollection();
        services.AddLedonGrpc();

        using var provider = services.BuildServiceProvider();

        // 默认表已注册（自定义映射追加即可，不需要替换）
        Assert.NotNull(provider.GetServices<IGrpcExceptionMapper>());
        Assert.Contains(provider.GetServices<IGrpcExceptionMapper>(),
            mapper => mapper is DefaultGrpcExceptionMapper);
    }

    [Fact]
    public void Error_interceptor_can_be_turned_off()
    {
        var services = new ServiceCollection();
        services.AddLedonGrpc(o => o.UseErrorInterceptor = false);

        using var provider = services.BuildServiceProvider();

        Assert.DoesNotContain(provider.GetServices<IGrpcExceptionMapper>(),
            mapper => mapper is DefaultGrpcExceptionMapper);
    }

    private sealed class DomainExceptionMapper : IGrpcExceptionMapper
    {
        public bool TryMap(Exception exception, out StatusCode statusCode, out string detail)
        {
            if (exception is InsufficientStockException stock)
            {
                statusCode = StatusCode.ResourceExhausted;
                detail = $"库存不足：{stock.Sku}";
                return true;
            }

            statusCode = StatusCode.Unknown;
            detail = "";
            return false;
        }
    }

    private sealed class InsufficientStockException(string sku) : Exception($"insufficient stock: {sku}")
    {
        public string Sku { get; } = sku;
    }
}
