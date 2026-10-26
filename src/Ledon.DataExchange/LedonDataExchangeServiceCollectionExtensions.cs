using Ledon.DataExchange;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>CSV/Excel 数据交换入口：<c>builder.Services.AddLedonDataExchange();</c></summary>
public static class LedonDataExchangeServiceCollectionExtensions
{
    public static IServiceCollection AddLedonDataExchange(
        this IServiceCollection services,
        Action<DataExchangeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new DataExchangeOptions();
        configure?.Invoke(options);

        if (options.MaxErrors < 0)
            throw new InvalidOperationException("DataExchangeOptions.MaxErrors 不能为负数");

        if (string.IsNullOrWhiteSpace(options.SheetName))
            throw new InvalidOperationException("DataExchangeOptions.SheetName 不能为空");

        services.TryAddSingleton(options);
        services.TryAddSingleton<IDataExchange, DataExchangeService>();

        return services;
    }
}
