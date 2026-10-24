namespace Ledon.Security;

/// <summary>
/// 登录凭证校验（应用实现：查库、调 IdP、对接遗留系统……）。
/// 实现类被自动发现并注入 <c>MapLedonAuthEndpoints</c> 的登录/刷新端点；
/// 不实现时端点返回 501 并提示需要它。
/// </summary>
public interface ILedonUserValidator
{
    /// <summary>校验用户名密码；成功返回身份信息（含权限），失败返回 null。</summary>
    Task<LedonUser?> ValidateAsync(string userName, string password, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按用户标识取回身份（刷新访问令牌时重新签发用）。默认未实现（null）——
    /// 实现了才能走完整的 login → access 过期 → refresh 换新双令牌流程。
    /// </summary>
    virtual Task<LedonUser?> FindByIdAsync(string userId, CancellationToken cancellationToken = default) =>
        Task.FromResult<LedonUser?>(null);
}

/// <summary>登录成功的用户身份。</summary>
/// <param name="UserId">用户标识（写入 sub 与 nameidentifier）。</param>
/// <param name="UserName">用户名。</param>
/// <param name="Permissions">权限列表（每条一个 permission claim）。</param>
public sealed record LedonUser(string UserId, string UserName, IReadOnlyList<string> Permissions);
