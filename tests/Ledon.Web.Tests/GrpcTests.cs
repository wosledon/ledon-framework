using System.Reflection;
using System.Text;
using Grpc.AspNetCore.Server;
using Grpc.Core;
using Ledon.Grpc;
using Ledon.Testing;
using Ledon.Web.Tests.Fixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>
/// gRPC 集成测试：服务发现（生成基类判据）、约定映射（幂等）、客户端注册。
/// 夹具模拟 protoc 生成代码的形状（抽象基类 + BindService + 具体实现），无需 .proto。
/// </summary>
public class GrpcTests : IDisposable
{
    private WebApplication? _app;

    [Fact]
    public void Discovery_finds_generated_shape_services_only()
    {
        var discovered = GrpcServiceDiscovery.Discover([typeof(GrpcTests).Assembly])
            .Select(type => type.FullName)
            .ToArray();

        Assert.Contains(typeof(FakeGreeter).FullName!, discovered);
        Assert.DoesNotContain(typeof(ChatHub).FullName!, discovered);          // SignalR Hub 不是 gRPC 服务
        Assert.DoesNotContain(typeof(HelloRequest).FullName!, discovered);     // 消息类型不是服务
    }

    [Fact]
    public void Health_service_is_discovered_when_enabled_and_absent_when_disabled()
    {
        var withHealth = new ServiceCollection();
        withHealth.AddLedonGrpc();   // UseHealthChecks 默认开
        using var providerWithHealth = withHealth.BuildServiceProvider();
        Assert.Contains(
            providerWithHealth.GetRequiredService<DiscoveredGrpcServices>().Services,
            type => type == typeof(global::Grpc.HealthCheck.HealthServiceImpl));

        var withoutHealth = new ServiceCollection();
        withoutHealth.AddLedonGrpc(o => o.UseHealthChecks = false);
        using var providerWithoutHealth = withoutHealth.BuildServiceProvider();
        Assert.DoesNotContain(
            providerWithoutHealth.GetRequiredService<DiscoveredGrpcServices>().Services,
            type => type == typeof(global::Grpc.HealthCheck.HealthServiceImpl));
    }

    [Fact]
    public void IsGrpcService_rejects_abstract_base_and_plain_classes()
    {
        Assert.True(GrpcServiceDiscovery.IsGrpcService(typeof(FakeGreeter)));
        Assert.False(GrpcServiceDiscovery.IsGrpcService(typeof(TestGreeter.TestGreeterBase)));   // 抽象基类自身
        Assert.False(GrpcServiceDiscovery.IsGrpcService(typeof(HelloRequest)));
        Assert.False(GrpcServiceDiscovery.IsGrpcService(typeof(PlainServiceLike)));
    }

    [Fact]
    public async Task Discovered_services_are_mapped_by_convention_and_mapping_is_idempotent()
    {
        await using var host = await LedonWebTestHost.StartAsync(
            configure: builder => builder.Services.AddLedonGrpc(),
            configureApp: app => app.MapLedonGrpcServices());

        var routes = host.App.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>()
            .Endpoints
            .Select(endpoint => (endpoint as Microsoft.AspNetCore.Routing.RouteEndpoint)?.RoutePattern?.RawText)
            .ToArray();

        // FakeGreeter.BindService 注册的 SayHello 方法被映射
        Assert.Contains(routes, pattern => pattern!.Contains("SayHello", StringComparison.Ordinal));

        // 标准健康检查服务（默认开）一并映射
        Assert.Contains(routes, pattern => pattern!.Contains("grpc.health.v1.Health", StringComparison.Ordinal));

        // 幂等：重复映射不产生重复端点
        host.App.MapLedonGrpcServices();

        var routesAgain = RoutePatterns(host.App)
            .Where(pattern => pattern?.Contains("SayHello", StringComparison.Ordinal) == true)
            .Count();

        Assert.Equal(1, routesAgain);
    }

    private static string?[] RoutePatterns(WebApplication app) =>
        app.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>()
            .Endpoints
            .Select(endpoint => (endpoint as RouteEndpoint)?.RoutePattern?.RawText)
            .ToArray();

    [Fact]
    public void Client_registration_resolves_typed_client()
    {
        var services = new ServiceCollection();
        services.AddLedonGrpcClient<FakeGreeterClient>("https://localhost:7001");

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<FakeGreeterClient>());
    }

    [Fact]
    public void Client_registration_requires_address()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(
            () => services.AddLedonGrpcClient<FakeGreeterClient>(""));
    }

    public void Dispose()
    {
        if (_app is not null)
        {
            _app.StopAsync().GetAwaiter().GetResult();
            _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        GC.SuppressFinalize(this);
    }
}

// ---------- 夹具：真实 protoc 生成代码（Protos/testgreeter.proto，命名空间 Ledon.Web.Tests.Fixtures） ----------

/// <summary>测试 gRPC 服务实现（继承生成基类 TestGreeter.TestGreeterBase）。</summary>
public class FakeGreeter : TestGreeter.TestGreeterBase
{
    public override Task<HelloReply> SayHello(HelloRequest request, ServerCallContext context) =>
        Task.FromResult(new HelloReply { Message = $"Hello {request.Name}" });
}

/// <summary>客户端类形状（CallInvoker 构造）：AddGrpcClient 可直接注册。</summary>
public class FakeGreeterClient
{
    public FakeGreeterClient(global::Grpc.Core.CallInvoker callInvoker) => CallInvoker = callInvoker;

    public global::Grpc.Core.CallInvoker CallInvoker { get; }
}

/// <summary>普通类（非 gRPC 服务，不应被发现）。</summary>
public class PlainServiceLike;
