using Ledon.Transactions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ledon.Application;

/// <summary>
/// 事务行为：把处理器包进 <see cref="IUnitOfWork"/>（未注册事务服务时直通）。
/// 业务处理器里只管写、调 <c>SaveChangesAsync</c>，提交/回滚由行为统一负责——
/// 与"统一事务入口"是同一种写法。
/// </summary>
public sealed class TransactionBehavior<TRequest>(IServiceProvider services, ILogger<TransactionBehavior<TRequest>> logger)
    : IPipelineBehavior<TRequest>
    where TRequest : notnull
{
    public async ValueTask<object?> HandleAsync(
        TRequest request, RequestHandlerDelegate next, CancellationToken cancellationToken = default)
    {
        var unitOfWork = services.GetService<IUnitOfWork>();

        if (unitOfWork is null)
        {
            logger.LogDebug("未注册 IUnitOfWork，请求 {Request} 不走事务", typeof(TRequest).Name);
            return await next().ConfigureAwait(false);
        }

        await using (unitOfWork)
        {
            return await unitOfWork.ExecuteAsync<object?>(async token =>
            {
                var response = await next().ConfigureAwait(false);
                await unitOfWork.SaveChangesAsync(token).ConfigureAwait(false);
                return response;
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
