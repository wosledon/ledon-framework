using System.Net;
using System.Net.Http.Headers;
using Ledon.ApiClients;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ledon.Web.Tests;

/// <summary>出站 API 客户端：令牌缓存、请求头传播与 DI 配置。</summary>
public class ApiClientTests
{
    [Fact]
    public async Task Token_provider_caches_and_invalidates()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"access_token":"token-1","expires_in":3600}"""),
        });

        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var provider = new ClientCredentialsTokenProvider(
            new ApiClientOptions
            {
                ClientId = "client",
                ClientSecret = "secret",
                TokenEndpoint = "https://identity.test/token",
                TokenRefreshSkew = TimeSpan.FromMinutes(1),
            },
            new StubHttpClientFactory(handler),
            time);

        Assert.Equal("token-1", await provider.GetAccessTokenAsync());
        Assert.Equal("token-1", await provider.GetAccessTokenAsync());
        Assert.Equal(1, handler.RequestCount);

        provider.Invalidate();
        Assert.Equal("token-1", await provider.GetAccessTokenAsync());
        Assert.Equal(2, handler.RequestCount);

        time.Now = time.Now.AddHours(2);
        Assert.Equal("token-1", await provider.GetAccessTokenAsync());
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task Handler_adds_bearer_and_correlation_id()
    {
        var capture = new StubHandler(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("token-xyz", request.Headers.Authorization?.Parameter);
            Assert.Equal("corr-123", request.Headers.GetValues("X-Correlation-ID").Single());

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var httpContext = new DefaultHttpContext();
        httpContext.TraceIdentifier = "trace-123";
        httpContext.Request.Headers["X-Correlation-ID"] = "corr-123";
        var accessor = new HttpContextAccessor { HttpContext = httpContext };

        var handler = new ApiClientAuthHandler(
            new StubTokenProvider("token-xyz"),
            new ApiClientOptions(),
            accessor)
        {
            InnerHandler = capture,
        };

        using var invoker = new HttpMessageInvoker(handler);
        using var response = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://orders.test/1"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, capture.RequestCount);
    }

    [Fact]
    public void Named_and_typed_clients_are_configured()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedonApiClient("orders", options =>
        {
            options.BaseAddress = "https://orders.test/";
            options.Timeout = TimeSpan.FromSeconds(5);
            options.DefaultHeaders["X-Api-Client"] = "ledon-tests";
        });
        services.AddLedonApiClient<OrdersClient>("typed", options => options.BaseAddress = "https://typed.test/");

        using var provider = services.BuildServiceProvider();

        var named = provider.GetRequiredService<IHttpClientFactory>().CreateClient("orders");
        Assert.Equal(new Uri("https://orders.test/"), named.BaseAddress);
        Assert.Equal(TimeSpan.FromSeconds(5), named.Timeout);
        Assert.Equal("ledon-tests", named.DefaultRequestHeaders.GetValues("X-Api-Client").Single());

        var typed = provider.GetRequiredService<OrdersClient>();
        Assert.Equal(new Uri("https://typed.test/"), typed.Http.BaseAddress);
    }

    private sealed class OrdersClient(HttpClient http)
    {
        public HttpClient Http { get; } = http;
    }

    private sealed class StubTokenProvider(string? token) : IClientCredentialsTokenProvider
    {
        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(token);

        public void Invalidate()
        {
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(response(request));
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
