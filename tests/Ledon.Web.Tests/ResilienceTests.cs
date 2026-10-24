using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Polly.CircuitBreaker;

namespace Ledon.Web.Tests;

/// <summary>
/// 弹性策略测试：通过 IHttpClientFactory 创建客户端（AddLedonResilience 默认接管全部客户端），
/// 用可控的假 handler 验证重试与熔断行为。
/// </summary>
public class ResilienceTests
{
    /// <summary>前 <paramref name="failures"/> 次返回 500，之后 200。</summary>
    private sealed class FlakyHandler(int failures) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _calls);
            var status = attempt <= failures
                ? HttpStatusCode.InternalServerError
                : HttpStatusCode.OK;

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("ok"),
            });
        }
    }

    private static ServiceProvider BuildProvider(HttpMessageHandler handler, Action<Ledon.Resilience.LedonResilienceOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLedonResilience(configure);
        services.AddHttpClient("test").ConfigurePrimaryHttpMessageHandler(() => handler);

        // 提供器由调用方持有：韧性管道随提供器生命周期，提前释放会导致已创建客户端失效
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Http_retry_recovers_from_transient_failures()
    {
        var handler = new FlakyHandler(2);   // 前两次 500

        using var provider = BuildProvider(handler, options =>
        {
            options.MaxRetryAttempts = 3;
            options.BaseDelay = TimeSpan.FromMilliseconds(10);   // 测试快速通道
        });

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("test");

        var response = await client.GetAsync("http://localhost/x");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, handler.Calls);   // 1 次原始 + 2 次重试
    }

    [Fact]
    public async Task Circuit_breaker_opens_and_short_circuits()
    {
        var handler = new FlakyHandler(int.MaxValue);   // 永远失败

        using var provider = BuildProvider(handler, options =>
        {
            options.MaxRetryAttempts = 0;                       // 不重试，快速累积失败
            options.MinimumThroughput = 3;
            options.FailureRatio = 0.5;
            options.BreakDuration = TimeSpan.FromSeconds(30);
        });

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("test");

        // 打到熔断打开（打开后的请求会快速失败，遇到即停）
        for (var i = 0; i < 10; i++)
        {
            try
            {
                await client.GetAsync("http://localhost/x");
            }
            catch (BrokenCircuitException)
            {
                break;
            }
        }

        var callsBefore = handler.Calls;

        // 熔断后：快速失败，不再到达 handler
        await Assert.ThrowsAsync<BrokenCircuitException>(
            async () => await client.GetAsync("http://localhost/x"));

        Assert.Equal(callsBefore, handler.Calls);
    }
}
