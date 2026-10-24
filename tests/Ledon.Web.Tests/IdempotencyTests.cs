using System.Net;
using System.Net.Http.Json;
using Ledon.Web;
using Ledon.Web.Tests.Fixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;

namespace Ledon.Web.Tests;

/// <summary>幂等中间件测试：重放、并发单飞、失败不缓存。</summary>
public class IdempotencyTests : IDisposable
{
    private WebApplication? _app;

    private async Task<HttpClient> StartAsync()
    {
        CountEndpoint.Reset();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddLedonWeb(o => o.UseIdempotency = true);
        builder.Services.AddDistributedMemoryCache();   // 幂等存储

        var app = builder.Build();
        app.UseLedonWeb();
        await app.StartAsync();
        _app = app;
        return app.GetTestClient();
    }

    private static HttpRequestMessage CountRequest(string key) => new(HttpMethod.Post, "/count")
    {
        Headers = { { "Idempotency-Key", key } },
    };

    [Fact]
    public async Task Idempotency_key_replays_response_and_skips_duplicate_execution()
    {
        var client = await StartAsync();

        // 3 个并发同 Key：只执行一次，响应一致（进程内单飞）
        var responses = await Task.WhenAll(Enumerable.Range(0, 3)
            .Select(_ => client.SendAsync(CountRequest("key-1"))));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));

        var counts = await Task.WhenAll(responses.Select(response =>
            response.Content.ReadFromJsonAsync<CountResponse>()));

        Assert.All(counts, count => Assert.Equal(1, count!.Count));

        // 顺序重放：带 Idempotency-Replayed 头，不再执行
        var replay = await client.SendAsync(CountRequest("key-1"));

        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(1, (await replay.Content.ReadFromJsonAsync<CountResponse>())!.Count);

        // 不同 Key：新执行
        var other = await client.SendAsync(CountRequest("key-2"));
        Assert.False(other.Headers.Contains("Idempotency-Replayed"));
        Assert.Equal(2, (await other.Content.ReadFromJsonAsync<CountResponse>())!.Count);
    }

    [Fact]
    public async Task Requests_without_idempotency_key_are_not_cached()
    {
        var client = await StartAsync();

        var first = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/count"));
        var second = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/count"));

        // 没带 Key：每次照常执行
        Assert.Equal(1, (await first.Content.ReadFromJsonAsync<CountResponse>())!.Count);
        Assert.Equal(2, (await second.Content.ReadFromJsonAsync<CountResponse>())!.Count);
    }

    [Fact]
    public async Task Idempotency_middleware_is_transparent_without_distributed_cache()
    {
        // 未注册 IDistributedCache：中间件透明跳过，请求照常执行
        CountEndpoint.Reset();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddLedonWeb(o => o.UseIdempotency = true);

        var app = builder.Build();
        app.UseLedonWeb();
        _app = app;
        await app.StartAsync();
        var client = app.GetTestClient();

        var first = await client.SendAsync(CountRequest("key-1"));
        var second = await client.SendAsync(CountRequest("key-1"));

        Assert.Equal(1, (await first.Content.ReadFromJsonAsync<CountResponse>())!.Count);
        Assert.Equal(2, (await second.Content.ReadFromJsonAsync<CountResponse>())!.Count);
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
