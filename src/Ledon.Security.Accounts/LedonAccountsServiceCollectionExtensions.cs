using Ledon.Security.Accounts;
using Ledon.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 账号模块入口：
/// <code>
/// builder.Services.AddLedonAccounts(o =&gt; o.RequireConfirmedEmail = true);
/// </code>
/// 提供注册/邮箱确认/忘记密码/重置密码端点与 PBKDF2 密码哈希、失败锁定。
/// </summary>
public static class LedonAccountsServiceCollectionExtensions
{
    public static IServiceCollection AddLedonAccounts(
        this IServiceCollection services,
        Action<AccountOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new AccountOptions();
        configure?.Invoke(options);

        if (options.MinPasswordLength < 6)
            throw new InvalidOperationException("AccountOptions.MinPasswordLength 至少为 6");

        if (options.MaxFailedAccessAttempts < 1)
            throw new InvalidOperationException("AccountOptions.MaxFailedAccessAttempts 至少为 1");

        if (options.LockoutDuration <= TimeSpan.Zero)
            throw new InvalidOperationException("AccountOptions.LockoutDuration 必须大于 0");

        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAccountStore, InMemoryAccountStore>();
        services.TryAddSingleton<IAccountTokenStore, InMemoryAccountTokenStore>();
        services.TryAddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.TryAddScoped<IAccountNotifier>(provider =>
            provider.GetService<Ledon.Notifications.INotificationSender>() is { } sender
                ? new NotificationAccountNotifier(sender)
                : NullAccountNotifier.Instance);
        services.TryAddScoped<AccountService>();

        if (options.RegisterUserValidator)
            services.AddScoped<Ledon.Security.ILedonUserValidator, AccountsUserValidator>();

        if (options.MapEndpoints)
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IEndpoint, AccountsEndpoints>());

        return services;
    }
}

/// <summary>账号端点（/auth/accounts/*）。</summary>
public sealed class AccountsEndpoints(
    IServiceScopeFactory scopeFactory,
    AccountOptions options) : IEndpoint
{
    public void Map(IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/auth/accounts");

        group.MapPost("/register", async (RegisterAccountRequest request, CancellationToken ct) =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();

            try
            {
                var result = await accounts.RegisterAsync(
                    request.UserName,
                    request.Email,
                    request.Password,
                    cancellationToken: ct);

                return Results.Created($"/auth/accounts/{result.Account.Id}", new
                {
                    result.Account.Id,
                    result.Account.UserName,
                    result.Account.Email,
                    result.Account.EmailConfirmed,
                    verificationToken = options.ExposeTokensInResponses ? result.EmailVerificationToken : null,
                });
            }
            catch (AccountConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (AccountValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/verify-email", async (VerifyEmailRequest request, CancellationToken ct) =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();

            return await accounts.ConfirmEmailAsync(request.AccountId, request.Token, ct)
                ? Results.NoContent()
                : Results.BadRequest(new { error = "令牌无效或已过期" });
        });

        group.MapPost("/resend-verification", async (ResendVerificationRequest request, CancellationToken ct) =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
            var token = await accounts.ResendEmailVerificationAsync(request.AccountId, ct);

            return Results.Accepted(value: new
            {
                sent = token is not null,
                verificationToken = options.ExposeTokensInResponses ? token : null,
            });
        });

        group.MapPost("/forgot-password", async (ForgotPasswordRequest request, CancellationToken ct) =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
            var token = await accounts.RequestPasswordResetAsync(request.Email, ct);

            // 不暴露账号是否存在
            return Results.Accepted(value: new
            {
                sent = true,
                resetToken = options.ExposeTokensInResponses ? token : null,
            });
        });

        group.MapPost("/reset-password", async (ResetPasswordRequest request, CancellationToken ct) =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();

            try
            {
                return await accounts.ResetPasswordAsync(request.Email, request.Token, request.NewPassword, ct)
                    ? Results.NoContent()
                    : Results.BadRequest(new { error = "令牌无效或已过期" });
            }
            catch (AccountValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}

public sealed record RegisterAccountRequest(string UserName, string? Email, string Password);

public sealed record VerifyEmailRequest(string AccountId, string Token);

public sealed record ResendVerificationRequest(string AccountId);

public sealed record ForgotPasswordRequest(string Email);

public sealed record ResetPasswordRequest(string Email, string Token, string NewPassword);
