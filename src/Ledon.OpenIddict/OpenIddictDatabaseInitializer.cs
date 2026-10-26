using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ledon.OpenIddict;

/// <summary>EF 共享存储初始化：宿主启动时确保 OpenIddict 表结构存在（开发/测试用）。</summary>
internal sealed class OpenIddictDatabaseInitializer(
    IServiceProvider provider,
    ILogger<OpenIddictDatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = provider.CreateAsyncScope();

        LedonOpenIddictDbContext context;

        try
        {
            context = scope.ServiceProvider.GetRequiredService<LedonOpenIddictDbContext>();
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                "OpenIddictOptions.Storage = EntityFrameworkCore 时，必须先注册 LedonOpenIddictDbContext：" +
                "builder.Services.AddDbContext<LedonOpenIddictDbContext>(o => o.UseSqlServer(...));", ex);
        }

        await using (context)
        {
            await context.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        }

        logger.LogInformation("OpenIddict 共享存储表结构已就绪");
    }

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
