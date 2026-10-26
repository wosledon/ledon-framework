using System.Net;
using Ledon.ApiClients;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Ledon.Web.Tests;

/// <summary>出站客户端边界：令牌端点失败与匿名请求。</summary>
public class ApiClientEdgeTests
{
    [Fact]
    public async Task Token_endpoint_failure_surfaces_clear_exception()
    {
        var provider = new ClientCredentialsTokenProvider(
            new ApiClientOptions
            {
                ClientId = "client",
                ClientSecret = "secret",
                TokenEndpoint = "https://identity.test/token",
            },
            new StubHttpClientFactory(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized))),
            TimeProvider.System);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAccessTokenAsync());
        Assert.Contains("401", error.Message);
    }

    [Fact]
    public async Task Handler_leaves_authorization_empty_without_token()
    {
        var capture = new StubHandler(request =>
        {
            Assert.Null(request.Headers.Authorization);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var handler = new ApiClientAuthHandler(
            new StubTokenProvider(null),
            new ApiClientOptions(),
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() })
        {
            InnerHandler = capture,
        };

        using var invoker = new HttpMessageInvoker(handler);
        using var response = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://orders.test/1"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
