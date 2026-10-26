using Ledon.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 搜索入口：
/// <code>
/// builder.Services.AddLedonSearch(o =&gt;
/// {
///     o.Provider = SearchProvider.Meilisearch;
///     o.Endpoint = "http://localhost:7700";
///     o.ApiKey = "...";
/// });
/// </code>
/// </summary>
public static class LedonSearchServiceCollectionExtensions
{
    public static IServiceCollection AddLedonSearch(
        this IServiceCollection services,
        Action<LedonSearchOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new LedonSearchOptions();
        configure?.Invoke(options);

        if (!Enum.IsDefined(options.Provider))
            throw new InvalidOperationException($"未定义的搜索引擎类型：{options.Provider}");

        if (options.Timeout <= TimeSpan.Zero)
            throw new InvalidOperationException("LedonSearchOptions.Timeout 必须大于 0");

        if (options.Provider == SearchProvider.Meilisearch
            && (string.IsNullOrWhiteSpace(options.Endpoint)
                || !Uri.TryCreate(options.Endpoint, UriKind.Absolute, out _)))
        {
            throw new InvalidOperationException("Meilisearch 需要配置合法的 Endpoint");
        }

        services.TryAddSingleton(options);

        services.TryAddSingleton<ISearchService>(_ =>
        {
            if (options.Provider == SearchProvider.InMemory)
                return new InMemorySearchService();

            var http = new HttpClient(
                options.HttpMessageHandler ?? new SocketsHttpHandler(),
                disposeHandler: options.HttpMessageHandler is null)
            {
                Timeout = options.Timeout,
            };

            return new MeilisearchSearchService(options, http);
        });

        return services;
    }
}
