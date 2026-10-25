namespace Ledon.Streaming;

/// <summary>鉴权上下文（一次推流/拉流请求）。</summary>
public sealed class StreamAuthContext
{
    /// <summary>流标识。</summary>
    public required StreamIdentity Identity { get; init; }

    /// <summary>推流还是拉流。</summary>
    public required StreamAction Action { get; init; }

    /// <summary>协议。</summary>
    public StreamProtocol? Protocol { get; init; }

    /// <summary>客户端 IP。</summary>
    public string? ClientIp { get; init; }

    /// <summary>客户端标识（媒体服务器分配）。</summary>
    public string? ClientId { get; init; }

    /// <summary>令牌（查询参数或头部携带）。</summary>
    public string? Token { get; init; }

    /// <summary>当前登录用户（Web 钩子场景可能没有）。</summary>
    public string? UserId { get; init; }

    /// <summary>租户（多租户隔离用）。</summary>
    public string? TenantId { get; init; }

    /// <summary>附加参数（查询参数全集）。</summary>
    public IReadOnlyDictionary<string, string>? Parameters { get; init; }
}

/// <summary>鉴权结果。</summary>
public sealed record StreamAuthResult(bool Allowed, string? Reason = null, string? UserId = null)
{
    /// <summary>放行。</summary>
    public static StreamAuthResult Allow(string? userId = null) => new(true, UserId: userId);

    /// <summary>拒绝（带原因，媒体服务器会断开该客户端）。</summary>
    public static StreamAuthResult Deny(string reason) => new(false, reason);
}

/// <summary>
/// 流鉴权策略：决定"谁能推哪条流、谁能看哪条流"。
/// 默认实现只验令牌；业务可实现本接口接自己的用户/租户/付费体系。
/// </summary>
public interface IStreamAuthorizer
{
    /// <summary>授权校验。</summary>
    ValueTask<StreamAuthResult> AuthorizeAsync(StreamAuthContext context, CancellationToken cancellationToken = default);
}

/// <summary>令牌鉴权器：验签名 + 过期 + 附加绑定（用户/租户）。</summary>
public sealed class TokenStreamAuthorizer(IStreamTokenService tokens) : IStreamAuthorizer
{
    public ValueTask<StreamAuthResult> AuthorizeAsync(StreamAuthContext context, CancellationToken cancellationToken = default)
    {
        // 附加绑定：上下文里的用户/租户必须与令牌签发时一致
        var extra = context.UserId ?? context.TenantId;

        if (tokens.Validate(context.Identity, context.Action, context.Token, extra))
            return ValueTask.FromResult(StreamAuthResult.Allow(context.UserId));

        return ValueTask.FromResult(StreamAuthResult.Deny("流令牌无效或已过期"));
    }
}
