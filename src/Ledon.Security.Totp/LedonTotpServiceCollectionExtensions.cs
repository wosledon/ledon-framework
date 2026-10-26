using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ledon.Security.Totp;

/// <summary>TOTP 注册：<c>builder.Services.AddLedonTotp();</c></summary>
public static class LedonTotpServiceCollectionExtensions
{
    public static IServiceCollection AddLedonTotp(
        this IServiceCollection services,
        Action<TotpOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new TotpOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<ITotpService, TotpService>();
        services.TryAddSingleton<IRecoveryCodeService, RecoveryCodeService>();

        return services;
    }
}
