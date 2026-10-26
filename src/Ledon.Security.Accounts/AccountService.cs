using System.Security.Cryptography;

namespace Ledon.Security.Accounts;

/// <summary>
/// 账号服务：注册、邮箱确认、登录锁定、密码重置。
/// 默认内存存储仅用于开发/单实例；生产请替换 <see cref="IAccountStore"/> 与 <see cref="IAccountTokenStore"/>。
/// </summary>
public sealed class AccountService(
    IAccountStore store,
    IPasswordHasher passwordHasher,
    IAccountTokenStore tokenStore,
    IAccountNotifier notifier,
    AccountOptions options,
    TimeProvider timeProvider)
{
    private readonly string _dummyHash = passwordHasher.Hash("ledon-timing-equalizer");

    public async Task<Account?> FindByIdAsync(string accountId, CancellationToken cancellationToken = default) =>
        await store.FindByIdAsync(accountId, cancellationToken).ConfigureAwait(false);

    public async Task<AccountRegistrationResult> RegisterAsync(
        string userName,
        string? email,
        string password,
        IReadOnlyList<string>? permissions = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedUserName = AccountNormalization.UserName(AccountValidation.RequireUserName(userName));
        var normalizedEmail = AccountValidation.NormalizeEmail(email, options.RequireEmail);
        AccountValidation.ValidatePassword(password, options);

        if (options.RequireConfirmedEmail && normalizedEmail is null)
            throw new AccountValidationException("要求邮箱确认时必须提供邮箱");

        if (await store.FindByUserNameAsync(normalizedUserName, cancellationToken).ConfigureAwait(false) is not null)
            throw new AccountConflictException("用户名已被占用");

        if (normalizedEmail is not null
            && await store.FindByEmailAsync(normalizedEmail, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new AccountConflictException("邮箱已被占用");
        }

        var account = new Account
        {
            Id = Guid.NewGuid().ToString("N"),
            UserName = userName.Trim(),
            NormalizedUserName = normalizedUserName,
            Email = email?.Trim(),
            NormalizedEmail = normalizedEmail,
            PasswordHash = passwordHasher.Hash(password),
            EmailConfirmed = !options.RequireConfirmedEmail,
            CreatedAtUtc = timeProvider.GetUtcNow(),
            Permissions = permissions?.ToArray() ?? [],
        };

        await store.AddAsync(account, cancellationToken).ConfigureAwait(false);

        string? verificationToken = null;

        if (options.RequireConfirmedEmail)
            verificationToken = await IssueTokenAsync(account, AccountTokenPurpose.EmailVerification, cancellationToken)
                .ConfigureAwait(false);

        return new AccountRegistrationResult(account, verificationToken);
    }

    public async Task<AccountLoginResult> AuthenticateAsync(
        string userName,
        string password,
        CancellationToken cancellationToken = default)
    {
        var normalized = AccountNormalization.UserName(userName ?? string.Empty);
        var account = await store.FindByUserNameAsync(normalized, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();

        if (account is null)
        {
            // 固定时间：不存在用户也做一次哈希校验，避免用户名枚举
            _ = passwordHasher.Verify(password ?? string.Empty, _dummyHash);
            return new AccountLoginResult(AccountLoginStatus.InvalidCredentials);
        }

        if (account.IsLockedOut(now))
            return new AccountLoginResult(AccountLoginStatus.LockedOut, LockoutEndUtc: account.LockoutEndUtc);

        if (options.RequireConfirmedEmail && !account.EmailConfirmed)
            return new AccountLoginResult(AccountLoginStatus.EmailNotConfirmed);

        var verification = passwordHasher.Verify(password ?? string.Empty, account.PasswordHash);

        if (!verification.Success)
        {
            account.AccessFailedCount++;

            if (account.AccessFailedCount >= options.MaxFailedAccessAttempts)
                account.LockoutEndUtc = now + options.LockoutDuration;

            await store.UpdateAsync(account, cancellationToken).ConfigureAwait(false);

            return new AccountLoginResult(
                account.IsLockedOut(now) ? AccountLoginStatus.LockedOut : AccountLoginStatus.InvalidCredentials,
                LockoutEndUtc: account.LockoutEndUtc);
        }

        account.AccessFailedCount = 0;
        account.LockoutEndUtc = null;
        account.LastLoginAtUtc = now;

        if (verification.NeedsRehash)
            account.PasswordHash = passwordHasher.Hash(password ?? string.Empty);

        await store.UpdateAsync(account, cancellationToken).ConfigureAwait(false);

        return new AccountLoginResult(
            AccountLoginStatus.Success,
            new Ledon.Security.LedonUser(account.Id, account.UserName, account.Permissions));
    }

    public async Task<bool> ConfirmEmailAsync(
        string accountId,
        string token,
        CancellationToken cancellationToken = default)
    {
        var account = await store.FindByIdAsync(accountId, cancellationToken).ConfigureAwait(false);

        if (account is null)
            return false;

        var consumed = await tokenStore.ConsumeAsync(
            AccountTokenHashing.Hash(token),
            AccountTokenPurpose.EmailVerification,
            accountId,
            cancellationToken).ConfigureAwait(false);

        if (!consumed)
            return false;

        account.EmailConfirmed = true;
        await store.UpdateAsync(account, cancellationToken).ConfigureAwait(false);

        return true;
    }

    public async Task<string?> ResendEmailVerificationAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        var account = await store.FindByIdAsync(accountId, cancellationToken).ConfigureAwait(false);

        if (account is null || account.EmailConfirmed || string.IsNullOrWhiteSpace(account.Email))
            return null;

        return await IssueTokenAsync(account, AccountTokenPurpose.EmailVerification, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>请求密码重置；账号不存在时返回 null（端点仍应返回 202，避免枚举）。</summary>
    public async Task<string?> RequestPasswordResetAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        var normalized = AccountNormalization.Email(email ?? string.Empty);
        var account = await store.FindByEmailAsync(normalized, cancellationToken).ConfigureAwait(false);

        if (account is null)
            return null;

        return await IssueTokenAsync(account, AccountTokenPurpose.PasswordReset, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> ResetPasswordAsync(
        string email,
        string token,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        var normalized = AccountNormalization.Email(email ?? string.Empty);
        var account = await store.FindByEmailAsync(normalized, cancellationToken).ConfigureAwait(false);

        if (account is null)
            return false;

        AccountValidation.ValidatePassword(newPassword, options);

        var consumed = await tokenStore.ConsumeAsync(
            AccountTokenHashing.Hash(token),
            AccountTokenPurpose.PasswordReset,
            account.Id,
            cancellationToken).ConfigureAwait(false);

        if (!consumed)
            return false;

        account.PasswordHash = passwordHasher.Hash(newPassword);
        account.AccessFailedCount = 0;
        account.LockoutEndUtc = null;
        await store.UpdateAsync(account, cancellationToken).ConfigureAwait(false);

        return true;
    }

    public async Task<bool> ChangePasswordAsync(
        string accountId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        var account = await store.FindByIdAsync(accountId, cancellationToken).ConfigureAwait(false);

        if (account is null || !passwordHasher.Verify(currentPassword ?? string.Empty, account.PasswordHash).Success)
            return false;

        AccountValidation.ValidatePassword(newPassword, options);

        account.PasswordHash = passwordHasher.Hash(newPassword);
        await store.UpdateAsync(account, cancellationToken).ConfigureAwait(false);

        return true;
    }

    public async Task<bool> UnlockAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var account = await store.FindByIdAsync(accountId, cancellationToken).ConfigureAwait(false);

        if (account is null)
            return false;

        account.AccessFailedCount = 0;
        account.LockoutEndUtc = null;
        await store.UpdateAsync(account, cancellationToken).ConfigureAwait(false);

        return true;
    }

    public async Task<bool> SetPermissionsAsync(
        string accountId,
        IReadOnlyList<string> permissions,
        CancellationToken cancellationToken = default)
    {
        var account = await store.FindByIdAsync(accountId, cancellationToken).ConfigureAwait(false);

        if (account is null)
            return false;

        account.Permissions = permissions.ToArray();
        await store.UpdateAsync(account, cancellationToken).ConfigureAwait(false);

        return true;
    }

    private async Task<string> IssueTokenAsync(
        Account account,
        AccountTokenPurpose purpose,
        CancellationToken cancellationToken)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        var lifetime = purpose == AccountTokenPurpose.EmailVerification
            ? options.EmailVerificationTokenLifetime
            : options.PasswordResetTokenLifetime;

        await tokenStore.CreateAsync(
            AccountTokenHashing.Hash(token),
            account.Id,
            purpose,
            timeProvider.GetUtcNow() + lifetime,
            cancellationToken).ConfigureAwait(false);

        await notifier.SendAsync(account, purpose, token, cancellationToken).ConfigureAwait(false);

        return token;
    }
}
