using Microsoft.EntityFrameworkCore;

namespace Ledon.OpenIddict;

/// <summary>
/// OpenIddict 共享存储上下文：多实例部署时把客户端/授权/scope/令牌放进同一个数据库。
/// <para>
/// 使用方式（必须在 <c>AddLedonOpenIddict</c> 之前注册，或至少在宿主启动前）：
/// <code>
/// builder.Services.AddDbContext&lt;LedonOpenIddictDbContext&gt;(o =&gt;
///     o.UseSqlServer(builder.Configuration.GetConnectionString("OpenIddict")));
///
/// builder.AddLedonOpenIddict(o =&gt;
/// {
///     o.Storage = OpenIddictStorage.EntityFrameworkCore;
///     o.EnsureDatabaseCreated = true;   // 开发/测试；生产建议用迁移
///     o.Clients.Add(...);
/// });
/// </code>
/// 多个实例连同一个库即可共享客户端、授权码、刷新令牌与令牌吊销状态。
/// 签名证书也要一致（<see cref="LedonOpenIddictOptions.SigningCertificatePath"/>），
/// 否则一个实例签发的访问令牌另一个实例验不过。
/// </para>
/// </summary>
public class LedonOpenIddictDbContext(DbContextOptions<LedonOpenIddictDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // OpenIddict 默认表结构（OpenIddictApplications / Authorizations / Scopes / Tokens）
        builder.UseOpenIddict();
    }
}
