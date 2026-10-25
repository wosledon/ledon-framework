using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace Ledon.AuditLog;

/// <summary>
/// 当前操作的审计上下文（AsyncLocal，随异步流流动）。
/// 由 <see cref="AuditContextMiddleware"/> 从 HTTP 请求填充；后台任务/定时任务
/// 可用 <see cref="BeginScope(string?, string?)"/> 手动声明操作人。
/// </summary>
public static class AuditContext
{
    private static readonly AsyncLocal<Scope?> Current = new();

    /// <summary>操作人标识。</summary>
    public static string? UserId => Current.Value?.UserId;

    /// <summary>操作人名。</summary>
    public static string? UserName => Current.Value?.UserName;

    /// <summary>请求/关联 ID。</summary>
    public static string? RequestId => Current.Value?.RequestId;

    /// <summary>来源 IP。</summary>
    public static string? IpAddress => Current.Value?.IpAddress;

    /// <summary>后台场景声明操作人（返回 <see cref="IDisposable"/>，用完释放）。</summary>
    public static IDisposable BeginScope(string? userId, string? userName = null)
    {
        var previous = Current.Value;
        Current.Value = new Scope(userId, userName, previous?.RequestId, previous?.IpAddress);

        return new Restore(previous);
    }

    internal static void Set(HttpContext context)
    {
        var user = context.User;
        var isAuthenticated = user?.Identity?.IsAuthenticated == true;

        Current.Value = new Scope(
            isAuthenticated ? user!.FindFirst("sub")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value : null,
            isAuthenticated ? user!.Identity?.Name : null,
            context.TraceIdentifier,
            context.Connection.RemoteIpAddress?.ToString());
    }

    private sealed record Scope(string? UserId, string? UserName, string? RequestId, string? IpAddress);

    private sealed class Restore(Scope? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            Current.Value = previous;
        }
    }
}

/// <summary>从 HTTP 请求填充 <see cref="AuditContext"/>（在映射端点之前执行）。</summary>
public sealed class AuditContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        AuditContext.Set(context);
        await next(context).ConfigureAwait(false);
    }
}
