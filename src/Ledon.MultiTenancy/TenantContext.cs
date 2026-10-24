using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;

namespace Ledon.MultiTenancy;

/// <summary>
/// 默认 <see cref="ITenantContext"/>：Web 请求优先取当前 HttpContext，
/// 其次取 AsyncLocal 环境租户（后台任务穿透），都取不到为 null（单租户模式）。
/// </summary>
public sealed class TenantContext : ITenantContext
{
    private static readonly AsyncLocal<string?> Ambient = new();

    private readonly IHttpContextAccessor _accessor;

    public TenantContext(IHttpContextAccessor accessor) => _accessor = accessor;

    /// <summary>环境租户：后台任务入队前设置，执行期间生效。</summary>
    public static IDisposable SetAmbient(string? tenantId)
    {
        var previous = Ambient.Value;
        Ambient.Value = tenantId;
        return new AmbientScope(previous);
    }

    /// <summary>
    /// 当前异步上下文的环境租户（后台任务穿透 + Web 请求经中间件设置）。
    /// 查询过滤等"不能闭包捕获 DbContext 实例"的场景（EF 模型缓存）用它取当前租户。
    /// </summary>
    public static string? AmbientTenantId => Ambient.Value;

    public string? TenantId =>
        _accessor.HttpContext?.Items[ItemKey] as string
        ?? Ambient.Value;

    internal const string ItemKey = "__LedonTenantId";

    private sealed class AmbientScope(string? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}
