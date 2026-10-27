using Ledon.DataExchange;
using Ledon.Ids;
using Ledon.Quotas;
using Ledon.Secrets;
using Ledon.Security.Authorization;
using Ledon.Security.Totp;
using Microsoft.Extensions.DependencyInjection;

Console.WriteLine("=== Ledon 能力演示（无需外部基础设施）===");
Console.WriteLine();

var services = new ServiceCollection();
services.AddLogging();
services.AddLedonIds(o => o.WorkerId = 1);
services.AddLedonTotp(o => o.Issuer = "Ledon Demo");
services.AddLedonDataExchange();
services.AddLedonInMemorySecrets(
[
    new KeyValuePair<string, string>("demo/db#password", "s3cret-from-memory"),
    new KeyValuePair<string, string>("demo/api#token", "token-123"),
]);
services.AddLedonQuotas(o =>
{
    o.DefaultLimit = 3;
    o.Window = TimeSpan.FromMinutes(1);
});
services.AddLedonAuthorization(o =>
{
    o.MapEndpoints = false;
    o.BootstrapAdminUserIds.Add("demo-admin");
});

await using var provider = services.BuildServiceProvider();

// ---------- 分布式 ID ----------
var ids = provider.GetRequiredService<IIdGenerator>();
Console.WriteLine("[Ids] ULID / GUID v7 / Snowflake");
Console.WriteLine($"  ULID       : {ids.NewUlid()}");
Console.WriteLine($"  GUID v7    : {ids.NewGuid()}");
Console.WriteLine($"  Snowflake  : {ids.NewSnowflake()}");
Console.WriteLine();

// ---------- TOTP ----------
var totp = provider.GetRequiredService<ITotpService>();
var recovery = provider.GetRequiredService<IRecoveryCodeService>();
var secret = totp.GenerateSecret();
var code = totp.GetCode(secret);
var uri = totp.BuildUri("demo@example.com", secret);
var recoveryCode = recovery.Generate()[0];

Console.WriteLine("[TOTP] RFC 6238 + otpauth URI + 恢复码");
Console.WriteLine($"  当前验证码 : {code}");
Console.WriteLine($"  校验通过   : {totp.Validate(secret, code)}");
Console.WriteLine($"  otpauth    : {uri}");
Console.WriteLine($"  恢复码     : {recoveryCode.Code}（仅展示一次，存储哈希）");
Console.WriteLine();

// ---------- 密钥 ----------
var secrets = provider.GetRequiredService<ISecretProvider>();
Console.WriteLine("[Secrets] 统一 ISecretProvider（示例为内存实现）");
Console.WriteLine($"  db password : {await secrets.GetRequiredSecretAsync("demo/db#password")}");
Console.WriteLine($"  api token   : {await secrets.GetRequiredSecretAsync("demo/api#token")}");
Console.WriteLine();

// ---------- 配额 ----------
var quotas = provider.GetRequiredService<QuotaService>();
Console.WriteLine("[Quotas] 固定窗口配额");
for (var i = 1; i <= 5; i++)
{
    var decision = await quotas.ConsumeAsync("demo-key", limit: 3, TimeSpan.FromMinutes(1));
    Console.WriteLine($"  第 {i} 次：allowed={decision.Allowed} remaining={decision.Remaining}");
}
Console.WriteLine();

// ---------- RBAC ----------
var rbac = provider.GetRequiredService<RoleAuthorizationService>();
await rbac.CreateRoleAsync("auditor", ["reports.read"]);
await rbac.AssignRoleAsync("demo-user", "auditor");

Console.WriteLine("[RBAC] 角色/权限/通配符/缓存");
Console.WriteLine($"  demo-admin 角色 : {string.Join(", ", await rbac.GetUserRolesAsync("demo-admin"))}");
Console.WriteLine($"  demo-admin 权限 : {string.Join(", ", await rbac.GetUserPermissionsAsync("demo-admin"))}");
Console.WriteLine($"  demo-user 读报表: {await rbac.HasPermissionAsync("demo-user", "reports.read")}");
Console.WriteLine($"  demo-user 删报表: {await rbac.HasPermissionAsync("demo-user", "reports.delete")}");
Console.WriteLine();

// ---------- 数据交换 ----------
var exchange = provider.GetRequiredService<IDataExchange>();
var orders = new[]
{
    new DemoOrder { Id = ids.NewGuid(), Amount = 12.5m, Note = "含,逗号" },
    new DemoOrder { Id = ids.NewGuid(), Amount = 99m, Note = "普通订单" },
};

await using var csv = new MemoryStream();
await exchange.ExportCsvAsync(csv, orders);
csv.Position = 0;

var imported = await exchange.ImportCsvAsync<DemoOrder>(csv);
Console.WriteLine("[DataExchange] CSV 导出 → 导入");
Console.WriteLine($"  导出 {orders.Length} 行，导入 {imported.Rows.Count} 行，错误 {imported.Errors.Count} 个");
Console.WriteLine($"  首行金额：{imported.Rows[0].Amount}");
Console.WriteLine();

Console.WriteLine("=== 演示结束：所有能力均为进程内实现，无付费组件 ===");

internal sealed class DemoOrder
{
    [DataColumn("订单号", 1)]
    public Guid Id { get; set; }

    [DataColumn("金额", 2)]
    public decimal Amount { get; set; }

    [DataColumn("备注", 3)]
    public string? Note { get; set; }
}
