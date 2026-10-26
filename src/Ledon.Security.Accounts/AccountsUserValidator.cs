using Ledon.Module.Abstractions;
using Ledon.Security;

namespace Ledon.Security.Accounts;

/// <summary>
/// 账号登录校验（可选）：把 <see cref="AccountService"/> 接入 <c>/auth/login</c>。
/// 标记 <see cref="DisableAutoDiscoveryAttribute"/>，需在 <c>AddLedonAccounts(o =&gt; o.RegisterUserValidator = true)</c> 时显式注册。
/// </summary>
[DisableAutoDiscovery]
public sealed class AccountsUserValidator(AccountService accounts) : ILedonUserValidator
{
    public async Task<LedonUser?> ValidateAsync(
        string userName,
        string password,
        CancellationToken cancellationToken = default)
    {
        var result = await accounts.AuthenticateAsync(userName, password, cancellationToken).ConfigureAwait(false);
        return result.User;
    }

    public async Task<LedonUser?> FindByIdAsync(string userId, CancellationToken cancellationToken = default)
    {
        var account = await accounts.FindByIdAsync(userId, cancellationToken).ConfigureAwait(false);

        return account is null ? null : new LedonUser(account.Id, account.UserName, account.Permissions);
    }
}
