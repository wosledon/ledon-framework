using Ledon.Application;
using Ledon.FeatureManagement;
using Ledon.Locking;
using Ledon.Transactions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Ledon.Web;
using Microsoft.Extensions.Logging;

namespace Ledon.Sample.Api;

/// <summary>
/// 平台服务演示端点：CQRS 管线 + 功能开关 + 分布式锁 + 本地化文案。
/// </summary>
public class PlatformDemoEndpoints : IEndpoint
{
    public void Map(IEndpointRouteBuilder builder)
    {
        // 1) CQRS：命令经"验证 → 日志 → 事务"管线（处理器只写业务）
        builder.MapPost("/demo/orders", async (CreateOrder command, IRequestSender sender, CancellationToken ct) =>
        {
            var id = await sender.SendAsync(command, ct);
            return Results.Created($"/demo/orders/{id}", new { id });
        });

        // 2) 功能开关：未启用返回 404（不暴露功能存在性）
        builder.MapGet("/demo/beta", () => Results.Ok(new { feature = "beta-report" }))
            .AddEndpointFilter(new FeatureGateFilter("beta-report"));

        // 3) 分布式锁：同一资源的并发请求被串行化
        builder.MapPost("/demo/pay/{orderId}", async (string orderId, IDistributedLock locker, CancellationToken ct) =>
        {
            await using var handle = await locker.AcquireAsync($"order:{orderId}", cancellationToken: ct);
            return Results.Ok(new { paid = orderId });
        });
    }
}

/// <summary>创建订单命令（自带校验：数量必须大于 0）。</summary>
public sealed record CreateOrder(string Sku, int Qty) : ICommand<Guid>, IValidatable
{
    public IReadOnlyCollection<string> Validate() =>
        Qty <= 0 ? ["数量必须大于 0"] : [];
}

/// <summary>处理器：经约定注册（同名接口规则不适用，靠 ICommandHandler&lt;,&gt; 识别）。</summary>
public class CreateOrderHandler(IUnitOfWork uow, ILogger<CreateOrderHandler> logger)
    : ICommandHandler<CreateOrder, Guid>
{
    public async Task<Guid> HandleAsync(CreateOrder command, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();

        logger.LogInformation("创建订单 {Sku} x {Qty}（事务由管线行为负责提交）", command.Sku, command.Qty);

        await uow.SaveChangesAsync(cancellationToken);
        return id;
    }
}
