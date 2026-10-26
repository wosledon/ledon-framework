using Ledon.Security;
using Ledon.Security.ApiKeys;
using Ledon.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 动态 API Key 入口：
/// <code>
/// builder.Services.AddLedonApiKeys(o =&gt; o.RequireAuthorization = true);
/// </code>
/// 提供创建/校验/吊销/列表与 <c>LedonApiKey</c> 认证方案；Key 只存 SHA-256 哈希。
/// </summary>
public static class LedonApiKeyServiceCollectionExtensions
{
    public static IServiceCollection AddLedonApiKeys(
        this IServiceCollection services,
        Action<ApiKeyManagementOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ApiKeyManagementOptions();
        configure?.Invoke(options);

        if (string.IsNullOrWhiteSpace(options.HeaderName))
            throw new InvalidOperationException("ApiKeyManagementOptions.HeaderName 不能为空");

        if (string.IsNullOrWhiteSpace(options.Prefix))
            throw new InvalidOperationException("ApiKeyManagementOptions.Prefix 不能为空");

        services.TryAddSingleton(options);
        services.TryAddSingleton<IApiKeyStore, InMemoryApiKeyStore>();
        services.TryAddSingleton<ApiKeyService>();

        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, DynamicApiKeyAuthenticationHandler>(ApiKeyDefaults.Scheme, null);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEndpoint, ApiKeyEndpoints>());

        return services;
    }
}

/// <summary>API Key 管理端点。</summary>
public sealed class ApiKeyEndpoints(
    ApiKeyService apiKeys,
    ApiKeyManagementOptions options) : IEndpoint
{
    public void Map(IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/auth/api-keys");

        if (options.RequireAuthorization)
            group.RequireAuthorization();

        group.MapGet("/", async (string? tenantId, CancellationToken ct) =>
        {
            var keys = await apiKeys.ListAsync(tenantId, ct);
            return Results.Ok(keys.Select(ToDto));
        });

        group.MapPost("/", async (CreateApiKeyRequest request, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "Name 不能为空" });

            var created = await apiKeys.CreateAsync(
                request.Name,
                request.Scopes,
                request.TenantId,
                request.ExpiresAtUtc,
                ct);

            // 明文只返回一次
            return Results.Created($"/auth/api-keys/{created.Key.Id}", new
            {
                key = ToDto(created.Key),
                plaintext = created.Plaintext,
            });
        });

        group.MapDelete("/{id:guid}", async (Guid id, CancellationToken ct) =>
            await apiKeys.RevokeAsync(id, ct) ? Results.NoContent() : Results.NotFound());
    }

    private static object ToDto(ApiKeyRecord key) => new
    {
        key.Id,
        key.Name,
        key.Prefix,
        key.TenantId,
        key.Scopes,
        key.CreatedAtUtc,
        key.ExpiresAtUtc,
        key.LastUsedAtUtc,
        key.Revoked,
    };
}

/// <summary>创建 API Key 请求。</summary>
public sealed record CreateApiKeyRequest(
    string Name,
    IReadOnlyCollection<string>? Scopes = null,
    string? TenantId = null,
    DateTimeOffset? ExpiresAtUtc = null);
