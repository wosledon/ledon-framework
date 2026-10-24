using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Ledon.Security;

// ---------- 端点契约 ----------

/// <summary>登录请求。</summary>
public sealed record LoginRequest(string UserName, string Password);

/// <summary>刷新请求。</summary>
public sealed record RefreshRequest(string RefreshToken);

/// <summary>令牌响应：访问令牌（JWT）+ 刷新令牌（不透明串，轮换用）。</summary>
public sealed record TokenResponse(string AccessToken, string RefreshToken, int ExpiresInSeconds);

// ---------- 端点映射 ----------

/// <summary>
/// 认证端点映射：<c>POST /auth/login</c>（用户名密码换双令牌）与
/// <c>POST /auth/refresh</c>（刷新令牌轮换取双令牌）。一行接线：
/// <code>app.MapLedonAuthEndpoints();</code>
/// 前置：<c>AddLedonSecurity()</c> + 实现 <see cref="ILedonUserValidator"/>。
/// </summary>
public static class LedonAuthEndpointExtensions
{
    public static IEndpointRouteBuilder MapLedonAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/auth/login", async (
            LoginRequest request,
            ILedonUserValidator validator,
            LedonSecurityOptions options,
            IJwtTokenGenerator jwt,
            IRefreshTokenService refresh,
            CancellationToken cancellationToken) =>
        {
            var user = await validator.ValidateAsync(request.UserName, request.Password, cancellationToken);
            if (user is null)
                return Results.Unauthorized();

            var access = jwt.Generate(user.UserId, user.UserName, user.Permissions);
            var refreshToken = await refresh.IssueAsync(user.UserId, cancellationToken);

            return Results.Ok(new TokenResponse(access, refreshToken, (int)options.Jwt.Expiration.TotalSeconds));
        });

        endpoints.MapPost("/auth/refresh", async (
            RefreshRequest request,
            ILedonUserValidator validator,
            LedonSecurityOptions options,
            IJwtTokenGenerator jwt,
            IRefreshTokenService refresh,
            CancellationToken cancellationToken) =>
        {
            // 校验（含重放检测：已吊销令牌再现 → 吊销全族 → null）
            var userId = await refresh.ValidateAsync(request.RefreshToken, cancellationToken);
            if (userId is null)
                return Results.Unauthorized();

            // 重新签发访问令牌需要身份（权限）——FindByIdAsync 未实现时明确告知
            var user = await validator.FindByIdAsync(userId, cancellationToken);
            if (user is null)
                return Results.Problem(
                    "刷新流程需要实现 ILedonUserValidator.FindByIdAsync（按用户标识重建身份与权限）",
                    statusCode: StatusCodes.Status501NotImplemented);

            var rotated = await refresh.RotateAsync(request.RefreshToken, cancellationToken);
            if (rotated is null)
                return Results.Unauthorized();

            var access = jwt.Generate(user.UserId, user.UserName, user.Permissions);
            return Results.Ok(new TokenResponse(access, rotated, (int)options.Jwt.Expiration.TotalSeconds));
        });

        return endpoints;
    }
}
