using Ledon.Security;
using Ledon.Security.Accounts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ledon.Web.Tests;

/// <summary>账号：注册/确认、登录锁定、密码重置与可选用户校验器。</summary>
public class AccountTests
{
    [Fact]
    public async Task Register_hashes_password_and_rejects_duplicates()
    {
        var (service, store, _) = CreateService();
        var result = await service.RegisterAsync("alice", "alice@example.com", "S3cret-pass");

        Assert.False(result.Account.EmailConfirmed);
        Assert.NotNull(result.EmailVerificationToken);
        Assert.DoesNotContain("S3cret-pass", result.Account.PasswordHash);

        await Assert.ThrowsAsync<AccountConflictException>(() =>
            service.RegisterAsync("ALICE", "other@example.com", "S3cret-pass"));
        await Assert.ThrowsAsync<AccountConflictException>(() =>
            service.RegisterAsync("bob", "ALICE@example.com", "S3cret-pass"));

        var stored = await store.FindByIdAsync(result.Account.Id);
        Assert.NotNull(stored);
        Assert.Equal(result.Account.PasswordHash, stored.PasswordHash);
    }

    [Fact]
    public async Task Login_requires_confirmed_email()
    {
        var (service, _, _) = CreateService();
        var registration = await service.RegisterAsync("alice", "alice@example.com", "S3cret-pass");

        var blocked = await service.AuthenticateAsync("alice", "S3cret-pass");
        Assert.Equal(AccountLoginStatus.EmailNotConfirmed, blocked.Status);

        Assert.False(await service.ConfirmEmailAsync(registration.Account.Id, "wrong-token"));
        Assert.True(await service.ConfirmEmailAsync(
            registration.Account.Id,
            registration.EmailVerificationToken!));

        var login = await service.AuthenticateAsync("alice", "S3cret-pass");
        Assert.Equal(AccountLoginStatus.Success, login.Status);
        Assert.Equal(registration.Account.Id, login.User!.UserId);
    }

    [Fact]
    public async Task Failed_attempts_lock_account_until_duration_passes()
    {
        var (service, _, time) = CreateService(options =>
        {
            options.RequireConfirmedEmail = false;
            options.MaxFailedAccessAttempts = 3;
            options.LockoutDuration = TimeSpan.FromMinutes(5);
        });

        await service.RegisterAsync("alice", "alice@example.com", "S3cret-pass");

        for (var i = 0; i < 2; i++)
            Assert.Equal(AccountLoginStatus.InvalidCredentials, (await service.AuthenticateAsync("alice", "wrong")).Status);

        var locked = await service.AuthenticateAsync("alice", "wrong");
        Assert.Equal(AccountLoginStatus.LockedOut, locked.Status);

        Assert.Equal(AccountLoginStatus.LockedOut, (await service.AuthenticateAsync("alice", "S3cret-pass")).Status);

        time.Now = time.Now.AddMinutes(6);
        Assert.Equal(AccountLoginStatus.Success, (await service.AuthenticateAsync("alice", "S3cret-pass")).Status);
    }

    [Fact]
    public async Task Password_reset_token_is_single_use()
    {
        var (service, _, _) = CreateService(options => options.RequireConfirmedEmail = false);
        await service.RegisterAsync("alice", "alice@example.com", "Old-pass123");

        var token = await service.RequestPasswordResetAsync("alice@example.com");
        Assert.NotNull(token);

        Assert.True(await service.ResetPasswordAsync("alice@example.com", token!, "New-pass123"));
        Assert.False(await service.ResetPasswordAsync("alice@example.com", token!, "Another-pass123"));

        Assert.Equal(AccountLoginStatus.InvalidCredentials, (await service.AuthenticateAsync("alice", "Old-pass123")).Status);
        Assert.Equal(AccountLoginStatus.Success, (await service.AuthenticateAsync("alice", "New-pass123")).Status);
    }

    [Fact]
    public async Task Optional_user_validator_is_registered_explicitly()
    {
        var services = new ServiceCollection();
        services.AddLedonAccounts(o =>
        {
            o.RequireConfirmedEmail = false;
            o.RegisterUserValidator = true;
        });

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
        await accounts.RegisterAsync("alice", "alice@example.com", "S3cret-pass");

        var validator = scope.ServiceProvider.GetRequiredService<ILedonUserValidator>();
        var user = await validator.ValidateAsync("alice", "S3cret-pass");

        Assert.NotNull(user);
        Assert.Equal("alice", user.UserName);
        Assert.NotNull(await validator.FindByIdAsync(user.UserId));
    }

    private static (AccountService Service, InMemoryAccountStore Store, ManualTimeProvider Time) CreateService(
        Action<AccountOptions>? configure = null)
    {
        var options = new AccountOptions { PasswordHashIterations = 10_000 };
        configure?.Invoke(options);

        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryAccountStore();
        var service = new AccountService(
            store,
            new Pbkdf2PasswordHasher(options),
            new InMemoryAccountTokenStore(time),
            NullAccountNotifier.Instance,
            options,
            time);

        return (service, store, time);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
