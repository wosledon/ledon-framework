using Ledon.Security.Accounts;
using Xunit;

namespace Ledon.Web.Tests;

/// <summary>账号边界：改密、重发确认、重置令牌过期与权限流转。</summary>
public class AccountEdgeTests
{
    [Fact]
    public async Task Change_password_requires_current_and_updates_login()
    {
        var (service, _) = CreateService(o => o.RequireConfirmedEmail = false);
        await service.RegisterAsync("alice", "alice@example.com", "Old-pass123");

        var userId = (await service.AuthenticateAsync("alice", "Old-pass123")).User!.UserId;

        Assert.False(await service.ChangePasswordAsync(userId, "wrong", "New-pass123"));
        Assert.True(await service.ChangePasswordAsync(userId, "Old-pass123", "New-pass123"));

        Assert.Equal(AccountLoginStatus.InvalidCredentials, (await service.AuthenticateAsync("alice", "Old-pass123")).Status);
        Assert.Equal(AccountLoginStatus.Success, (await service.AuthenticateAsync("alice", "New-pass123")).Status);
    }

    [Fact]
    public async Task Resend_verification_invalidates_old_token_and_stops_after_confirmation()
    {
        var (service, _) = CreateService(o => o.RequireConfirmedEmail = true);
        var registration = await service.RegisterAsync("bob", "bob@example.com", "S3cret-pass");

        var resent = await service.ResendEmailVerificationAsync(registration.Account.Id);
        Assert.NotNull(resent);
        Assert.False(await service.ConfirmEmailAsync(registration.Account.Id, registration.EmailVerificationToken!));
        Assert.True(await service.ConfirmEmailAsync(registration.Account.Id, resent!));

        Assert.Null(await service.ResendEmailVerificationAsync(registration.Account.Id));
    }

    [Fact]
    public async Task Expired_password_reset_token_is_rejected()
    {
        var (service, clock) = CreateService(o =>
        {
            o.RequireConfirmedEmail = false;
            o.PasswordResetTokenLifetime = TimeSpan.FromMinutes(5);
        });

        await service.RegisterAsync("carol", "carol@example.com", "Old-pass123");
        var token = await service.RequestPasswordResetAsync("carol@example.com");

        clock.Now = clock.Now.AddMinutes(10);

        Assert.False(await service.ResetPasswordAsync("carol@example.com", token!, "New-pass123"));
        Assert.Equal(AccountLoginStatus.Success, (await service.AuthenticateAsync("carol", "Old-pass123")).Status);
    }

    [Fact]
    public async Task Permissions_are_returned_on_successful_login()
    {
        var (service, _) = CreateService(o => o.RequireConfirmedEmail = false);
        await service.RegisterAsync("dave", "dave@example.com", "S3cret-pass", ["orders.read", "users.read"]);

        var login = await service.AuthenticateAsync("dave", "S3cret-pass");

        Assert.Equal(AccountLoginStatus.Success, login.Status);
        Assert.Contains("orders.read", login.User!.Permissions);
    }

    private static (AccountService Service, ManualTimeProvider Clock) CreateService(
        Action<AccountOptions> configure)
    {
        var options = new AccountOptions { PasswordHashIterations = 10_000 };
        configure(options);

        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryAccountStore();
        var service = new AccountService(
            store,
            new Pbkdf2PasswordHasher(options),
            new InMemoryAccountTokenStore(clock),
            NullAccountNotifier.Instance,
            options,
            clock);

        return (service, clock);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
