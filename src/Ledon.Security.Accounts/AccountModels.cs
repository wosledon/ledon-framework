using System.Collections.Concurrent;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;

namespace Ledon.Security.Accounts;

/// <summary>账号模块选项。</summary>
public sealed class AccountOptions
{
    /// <summary>是否要求邮箱确认后才能登录，默认 <c>true</c>。</summary>
    public bool RequireConfirmedEmail { get; set; } = true;

    /// <summary>注册是否必须提供邮箱，默认 <c>true</c>。</summary>
    public bool RequireEmail { get; set; } = true;

    /// <summary>密码最小长度，默认 8。</summary>
    public int MinPasswordLength { get; set; } = 8;

    /// <summary>密码最大长度，默认 128。</summary>
    public int MaxPasswordLength { get; set; } = 128;

    /// <summary>连续失败多少次后锁定，默认 5。</summary>
    public int MaxFailedAccessAttempts { get; set; } = 5;

    /// <summary>锁定时长，默认 5 分钟。</summary>
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>邮箱确认令牌有效期，默认 24 小时。</summary>
    public TimeSpan EmailVerificationTokenLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>密码重置令牌有效期，默认 1 小时。</summary>
    public TimeSpan PasswordResetTokenLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>PBKDF2 迭代次数，默认 100000。</summary>
    public int PasswordHashIterations { get; set; } = 100_000;

    /// <summary>
    /// 是否把 <see cref="AccountsUserValidator"/> 注册为 <c>ILedonUserValidator</c>（供 /auth/login 使用），
    /// 默认 <c>false</c>（避免覆盖宿主已有校验器；需要时显式打开）。
    /// </summary>
    public bool RegisterUserValidator { get; set; }

    /// <summary>是否映射账号端点（/auth/accounts/*），默认 <c>true</c>。</summary>
    public bool MapEndpoints { get; set; } = true;

    /// <summary>
    /// 是否在 HTTP 响应中返回邮箱确认/密码重置令牌，默认 <c>false</c>。
    /// 仅本地开发/测试开启；生产应由 <see cref="IAccountNotifier"/> 发送邮件。
    /// </summary>
    public bool ExposeTokensInResponses { get; set; }
}

/// <summary>账号实体。</summary>
public sealed class Account
{
    public required string Id { get; set; }

    public required string UserName { get; set; }

    public string NormalizedUserName { get; set; } = string.Empty;

    public string? Email { get; set; }

    public string? NormalizedEmail { get; set; }

    public required string PasswordHash { get; set; }

    public bool EmailConfirmed { get; set; }

    public int AccessFailedCount { get; set; }

    public DateTimeOffset? LockoutEndUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? LastLoginAtUtc { get; set; }

    public IReadOnlyList<string> Permissions { get; set; } = [];

    public bool IsLockedOut(DateTimeOffset now) => LockoutEndUtc is { } end && end > now;
}

/// <summary>账号存储（默认内存；可替换为数据库实现）。</summary>
public interface IAccountStore
{
    Task<Account?> FindByIdAsync(string accountId, CancellationToken cancellationToken = default);

    Task<Account?> FindByUserNameAsync(string normalizedUserName, CancellationToken cancellationToken = default);

    Task<Account?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default);

    Task AddAsync(Account account, CancellationToken cancellationToken = default);

    Task UpdateAsync(Account account, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Account>> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>默认内存账号存储。</summary>
public sealed class InMemoryAccountStore : IAccountStore
{
    private readonly ConcurrentDictionary<string, Account> _accounts = new(StringComparer.Ordinal);

    public Task<Account?> FindByIdAsync(string accountId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_accounts.TryGetValue(accountId, out var account) ? account : null);

    public Task<Account?> FindByUserNameAsync(string normalizedUserName, CancellationToken cancellationToken = default) =>
        Task.FromResult(_accounts.Values.FirstOrDefault(account =>
            string.Equals(account.NormalizedUserName, normalizedUserName, StringComparison.Ordinal)));

    public Task<Account?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default) =>
        Task.FromResult(_accounts.Values.FirstOrDefault(account =>
            string.Equals(account.NormalizedEmail, normalizedEmail, StringComparison.Ordinal)));

    public Task AddAsync(Account account, CancellationToken cancellationToken = default)
    {
        if (!_accounts.TryAdd(account.Id, account))
            throw new InvalidOperationException($"账号已存在：{account.Id}");

        return Task.CompletedTask;
    }

    public Task UpdateAsync(Account account, CancellationToken cancellationToken = default)
    {
        _accounts[account.Id] = account;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Account>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Account>>(_accounts.Values.OrderBy(account => account.Id, StringComparer.Ordinal).ToArray());
}

/// <summary>密码校验结果。</summary>
public sealed record PasswordVerificationResult(bool Success, bool NeedsRehash = false);

/// <summary>密码哈希器。</summary>
public interface IPasswordHasher
{
    string Hash(string password);

    PasswordVerificationResult Verify(string password, string passwordHash);
}

/// <summary>PBKDF2-SHA256 密码哈希（格式：pbkdf2-sha256$iterations$salt$hash）。</summary>
public sealed class Pbkdf2PasswordHasher(AccountOptions options) : IPasswordHasher
{
    private const string Prefix = "pbkdf2-sha256";
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        var iterations = Math.Max(10_000, options.PasswordHashIterations);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashSize);

        return string.Join('$', Prefix, iterations, Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    public PasswordVerificationResult Verify(string password, string passwordHash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(passwordHash))
            return new PasswordVerificationResult(false);

        var parts = passwordHash.Split('$');

        if (parts.Length != 4
            || !string.Equals(parts[0], Prefix, StringComparison.Ordinal)
            || !int.TryParse(parts[1], out var iterations)
            || iterations <= 0)
        {
            return new PasswordVerificationResult(false);
        }

        byte[] salt;
        byte[] expected;

        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return new PasswordVerificationResult(false);
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        var success = CryptographicOperations.FixedTimeEquals(actual, expected);

        return new PasswordVerificationResult(success, success && iterations < options.PasswordHashIterations);
    }
}

/// <summary>令牌用途。</summary>
public enum AccountTokenPurpose
{
    EmailVerification,
    PasswordReset,
}

/// <summary>账号令牌存储（默认内存；多实例请替换为共享存储）。</summary>
public interface IAccountTokenStore
{
    Task CreateAsync(
        string tokenHash,
        string accountId,
        AccountTokenPurpose purpose,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>消费令牌（一次性）；成功返回账号 ID。</summary>
    Task<bool> ConsumeAsync(
        string tokenHash,
        AccountTokenPurpose purpose,
        string? accountId = null,
        CancellationToken cancellationToken = default);

    /// <summary>移除某账号某用途的全部令牌（重发确认/重置时使旧令牌失效）。</summary>
    Task RemoveAllAsync(
        string accountId,
        AccountTokenPurpose purpose,
        CancellationToken cancellationToken = default);
}

/// <summary>默认内存令牌存储。</summary>
public sealed class InMemoryAccountTokenStore(TimeProvider timeProvider) : IAccountTokenStore
{
    private readonly ConcurrentDictionary<string, TokenEntry> _tokens = new(StringComparer.Ordinal);

    public Task CreateAsync(
        string tokenHash,
        string accountId,
        AccountTokenPurpose purpose,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken = default)
    {
        CleanupExpired();
        _tokens[tokenHash] = new TokenEntry(accountId, purpose, expiresAtUtc);

        return Task.CompletedTask;
    }

    public Task<bool> ConsumeAsync(
        string tokenHash,
        AccountTokenPurpose purpose,
        string? accountId = null,
        CancellationToken cancellationToken = default)
    {
        if (!_tokens.TryRemove(tokenHash, out var entry))
            return Task.FromResult(false);

        if (entry.Purpose != purpose
            || entry.ExpiresAtUtc <= timeProvider.GetUtcNow()
            || (accountId is not null && !string.Equals(entry.AccountId, accountId, StringComparison.Ordinal)))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    public Task RemoveAllAsync(
        string accountId,
        AccountTokenPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        foreach (var (hash, entry) in _tokens)
        {
            if (string.Equals(entry.AccountId, accountId, StringComparison.Ordinal) && entry.Purpose == purpose)
                _tokens.TryRemove(hash, out _);
        }

        return Task.CompletedTask;
    }

    private void CleanupExpired()
    {
        var now = timeProvider.GetUtcNow();

        foreach (var (hash, entry) in _tokens)
        {
            if (entry.ExpiresAtUtc <= now)
                _tokens.TryRemove(hash, out _);
        }
    }

    private sealed record TokenEntry(string AccountId, AccountTokenPurpose Purpose, DateTimeOffset ExpiresAtUtc);
}

/// <summary>账号通知（邮箱确认/密码重置）；默认空实现，接入 Ledon.Notifications 时自动发送邮件。</summary>
public interface IAccountNotifier
{
    Task SendAsync(Account account, AccountTokenPurpose purpose, string token, CancellationToken cancellationToken = default);
}

/// <summary>空通知实现。</summary>
public sealed class NullAccountNotifier : IAccountNotifier
{
    public static NullAccountNotifier Instance { get; } = new();

    public Task SendAsync(Account account, AccountTokenPurpose purpose, string token, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

/// <summary>经 <c>Ledon.Notifications</c> 发送账号邮件。</summary>
public sealed class NotificationAccountNotifier(Ledon.Notifications.INotificationSender sender) : IAccountNotifier
{
    public Task SendAsync(Account account, AccountTokenPurpose purpose, string token, CancellationToken cancellationToken = default)
    {
        var (subject, template) = purpose switch
        {
            AccountTokenPurpose.EmailVerification => ("确认你的邮箱", "account.email-verification"),
            _ => ("重置你的密码", "account.password-reset"),
        };

        var body = $"你好 {account.UserName}：\n\n{purpose switch
        {
            AccountTokenPurpose.EmailVerification => "请使用以下令牌确认邮箱：",
            _ => "请使用以下令牌重置密码：",
        }}\n\n{token}\n\n如非本人操作请忽略本邮件。";

        return sender.SendAsync(
            new Ledon.Notifications.Notification("email", account.Email ?? string.Empty, subject, body, template),
            cancellationToken);
    }
}

/// <summary>账号校验错误。</summary>
public sealed class AccountValidationException(string message) : InvalidOperationException(message);

/// <summary>账号冲突（用户名/邮箱已存在）。</summary>
public sealed class AccountConflictException(string message) : InvalidOperationException(message);

/// <summary>登录结果状态。</summary>
public enum AccountLoginStatus
{
    Success,
    InvalidCredentials,
    LockedOut,
    EmailNotConfirmed,
}

/// <summary>登录结果。</summary>
public sealed record AccountLoginResult(
    AccountLoginStatus Status,
    Ledon.Security.LedonUser? User = null,
    DateTimeOffset? LockoutEndUtc = null);

/// <summary>注册结果（确认令牌仅返回给宿主，不应直接暴露在 HTTP 响应）。</summary>
public sealed record AccountRegistrationResult(Account Account, string? EmailVerificationToken);

internal static class AccountNormalization
{
    public static string UserName(string value) => value.Trim().ToUpperInvariant();

    public static string Email(string value) => value.Trim().ToUpperInvariant();
}

internal static class AccountTokenHashing
{
    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}

internal static class AccountValidation
{
    public static string RequireUserName(string? userName)
    {
        var value = userName?.Trim();

        if (string.IsNullOrEmpty(value) || value.Length is < 3 or > 64)
            throw new AccountValidationException("用户名长度必须为 3-64 个字符");

        if (value.Any(char.IsWhiteSpace))
            throw new AccountValidationException("用户名不能包含空白字符");

        return value;
    }

    public static string? NormalizeEmail(string? email, bool required)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            if (required)
                throw new AccountValidationException("邮箱不能为空");

            return null;
        }

        var value = email.Trim();

        try
        {
            _ = new MailAddress(value);
        }
        catch (FormatException)
        {
            throw new AccountValidationException("邮箱格式不正确");
        }

        return AccountNormalization.Email(value);
    }

    public static void ValidatePassword(string password, AccountOptions options)
    {
        if (string.IsNullOrEmpty(password) || password.Length < options.MinPasswordLength)
            throw new AccountValidationException($"密码至少 {options.MinPasswordLength} 个字符");

        if (password.Length > options.MaxPasswordLength)
            throw new AccountValidationException($"密码最多 {options.MaxPasswordLength} 个字符");
    }
}
