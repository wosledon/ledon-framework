using Ledon.Module.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Data;

/// <summary>
/// 数据库前置初始化：作为 <see cref="ILedonPreInitialization"/> 先于所有模块执行。
/// 有挂起迁移则 Migrate，否则 EnsureCreated——两种工作流（迁移/快速建库）都无需手动处理。
/// </summary>
internal sealed class DbInitializer(IServiceProvider services, LedonDataOptions options)
    : ILedonPreInitialization
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (!options.AutoMigrate)
            return;

        await using var scope = services.CreateAsyncScope();

        foreach (var context in scope.ServiceProvider.GetServices<ILedonDbContext>())
        {
            if (context is not DbContext db)
                continue;

            var pending = await db.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false);
            if (pending.Any())
                await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            else
                await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
