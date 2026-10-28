using Ledon.Security.Accounts;

namespace Ledon.Web.Tests;

/// <summary>账号边界补充：重复注册、邮箱确认、锁定与解锁、未知用户。</summary>
public class AccountsEdge2Tests
{
    private static (AccountService Service, ManualTimeProvider Clock) CreateService(
        Action<AccountOptions>? configure = null)
    {
        var options = new AccountOptions { PasswordHashIterations = 10_000 };
        configure?.Invoke(options);

        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var service = new AccountService(
            new InMemoryAccountStore(),
            new Pbkdf2PasswordHasher(options),
            new InMemoryAccountTokenStore(clock),
            NullAccountNotifier.Instance,
            options,
            clock);

        return (service, clock);
    }

    [Fact]
    public async Task Duplicate_user_name_and_email_are_rejected()
    {
        var (service, _) = CreateService();
        await service.RegisterAsync("alice", "alice@example.com", "S3cret-pass");

        await Assert.ThrowsAsync<AccountConflictException>(
            () => service.RegisterAsync("alice", "other@example.com", "S3cret-pass"));
        await Assert.ThrowsAsync<AccountConflictException>(
            () => service.RegisterAsync("other", "alice@example.com", "S3cret-pass"));
    }

    [Fact]
    public async Task Login_requires_confirmed_email_when_configured()
    {
        var (service, _) = CreateService(options => options.RequireConfirmedEmail = true);
        await service.RegisterAsync("bob", "bob@example.com", "S3cret-pass");

        var login = await service.AuthenticateAsync("bob", "S3cret-pass");

        Assert.Equal(AccountLoginStatus.EmailNotConfirmed, login.Status);
    }

    [Fact]
    public async Task Repeated_failures_lock_the_account_and_unlock_works()
    {
        var (service, clock) = CreateService(options =>
        {
            options.RequireConfirmedEmail = false;
            options.MaxFailedAccessAttempts = 2;
            options.LockoutDuration = TimeSpan.FromMinutes(5);
        });
        var registration = await service.RegisterAsync("carol", "carol@example.com", "S3cret-pass");

        Assert.Equal(AccountLoginStatus.InvalidCredentials, (await service.AuthenticateAsync("carol", "wrong")).Status);
        Assert.Equal(AccountLoginStatus.LockedOut, (await service.AuthenticateAsync("carol", "wrong")).Status);

        Assert.True(await service.UnlockAsync(registration.Account.Id));
        Assert.Equal(AccountLoginStatus.Success, (await service.AuthenticateAsync("carol", "S3cret-pass")).Status);

        clock.Now = clock.Now.AddMinutes(10);
        Assert.Equal(AccountLoginStatus.Success, (await service.AuthenticateAsync("carol", "S3cret-pass")).Status);
    }

    [Fact]
    public async Task Unknown_user_and_invalid_tokens_are_rejected()
    {
        var (service, _) = CreateService();

        Assert.Equal(AccountLoginStatus.InvalidCredentials, (await service.AuthenticateAsync("missing", "x")).Status);
        Assert.Null(await service.RequestPasswordResetAsync("missing@example.com"));
        Assert.False(await service.ConfirmEmailAsync("missing", "token"));
        Assert.False(await service.ResetPasswordAsync("missing@example.com", "token", "S3cret-pass"));
        Assert.False(await service.UnlockAsync("missing"));
    }

    [Fact]
    public async Task Permissions_update_is_reflected_on_next_login()
    {
        var (service, _) = CreateService(options => options.RequireConfirmedEmail = false);
        var registration = await service.RegisterAsync("erin", "erin@example.com", "S3cret-pass");

        Assert.True(await service.SetPermissionsAsync(registration.Account.Id, ["orders.read", "users.read"]));
        var login = await service.AuthenticateAsync("erin", "S3cret-pass");

        Assert.Equal(AccountLoginStatus.Success, login.Status);
        Assert.Contains("orders.read", login.User!.Permissions);
    }

    [Fact]
    public async Task Permissions_update_for_missing_account_returns_false()
    {
        var (service, _) = CreateService();

        Assert.False(await service.SetPermissionsAsync("missing", ["orders.read"]));
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
