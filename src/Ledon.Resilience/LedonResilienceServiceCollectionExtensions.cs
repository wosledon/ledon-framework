using System.Net;
using Ledon.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 弹性策略入口。日常用法一行：
/// <code>builder.Services.AddLedonResilience();</code>
/// 注册名为 <c>ledon-http</c> 的韧性管道（指数退避重试 + 尝试超时 + 熔断），
/// 并默认挂到全部 <see cref="System.Net.Http.HttpClient"/>（IHttpClientFactory 创建）；
/// 自定义客户端可 <c>.AddResilienceHandler("ledon-http")</c> 显式接入。
/// </summary>
public static class LedonResilienceServiceCollectionExtensions
{
    /// <summary>HTTP 韧性管道名（自定义接入时用）。</summary>
    public const string HttpPipelineName = "ledon-http";

    public static IServiceCollection AddLedonResilience(
        this IServiceCollection services,
        Action<LedonResilienceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new LedonResilienceOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        // 策略定义只写一份：命名管道（供直接注入 ResiliencePipeline<T> 的代码用）
        // 与 HttpClient 接入（每个客户端内联构建）共享。
        // 注意：AddResilienceHandler 的管道只由其 configure 参数构建，
        // 不会自动执行同名命名管道（空 configure = 空管道，实测行为）。
        void ConfigurePipeline(ResiliencePipelineBuilder<HttpResponseMessage> builder)
        {
            // MaxRetryAttempts <= 0 表示不启用重试（Polly 要求 1..int.MaxValue，0 会校验失败）
            if (options.MaxRetryAttempts > 0)
            {
                builder.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
                {
                    MaxRetryAttempts = options.MaxRetryAttempts,
                    BackoffType = DelayBackoffType.Exponential,
                    Delay = options.BaseDelay,
                    UseJitter = true,
                    ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                        .Handle<HttpRequestException>()
                        .Handle<TaskCanceledException>()
                        .HandleResult(response =>
                            response.StatusCode == HttpStatusCode.TooManyRequests
                            || (int)response.StatusCode >= 500),
                });
            }

            builder
                .AddTimeout(new TimeoutStrategyOptions
                {
                    Timeout = options.AttemptTimeout,
                })
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
                {
                    FailureRatio = options.FailureRatio,
                    MinimumThroughput = options.MinimumThroughput,
                    BreakDuration = options.BreakDuration,
                    ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                        .Handle<HttpRequestException>()
                        .HandleResult(response => (int)response.StatusCode >= 500),
                });
        }

        // 命名管道（注意：泛型重载是 <TKey, TResult> 两个类型参数，
        // 少传一个会绑到 TKey 重载拿到非泛型 builder——重载解析陷阱）
        services.AddResiliencePipeline<string, HttpResponseMessage>(HttpPipelineName, ConfigurePipeline);

        // 全部 HttpClient 默认接入（可在具体客户端上覆盖）
        if (options.ApplyToAllHttpClients)
        {
            services.ConfigureHttpClientDefaults(client =>
                client.AddResilienceHandler(HttpPipelineName, (builder, _) => ConfigurePipeline(builder)));
        }

        return services;
    }
}
