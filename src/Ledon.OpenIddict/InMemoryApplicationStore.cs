using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;

namespace Ledon.OpenIddict;

/// <summary>
/// 内存应用存储（配置驱动、只读）：客户端定义来自 <see cref="OpenIddictClientDefinition"/>。
/// 可写属性直接落在 Descriptor 上；只读属性（Permissions 等）存进属性袋。
/// 写方法抛 <see cref="NotSupportedException"/>——需要动态注册请换 OpenIddict 官方的 EF/Redis 存储。
/// </summary>
internal sealed class InMemoryApplicationStore : IOpenIddictApplicationStore<OpenIddictApplicationDescriptor>
{
    private readonly ConcurrentDictionary<string, OpenIddictApplicationDescriptor> _applications = new(StringComparer.Ordinal);

    public InMemoryApplicationStore(LedonOpenIddictOptions options)
    {
        // 客户端由 OpenIddictClientInitializer 在宿主启动时经管理器创建（密钥哈希约定）；
        // 此处仅保存选项引用供权限构建使用。
        _options = options;
    }

    private readonly LedonOpenIddictOptions _options;

    internal static ImmutableArray<string> BuildPermissions(
        LedonOpenIddictOptions options,
        OpenIddictClientDefinition client)
    {
        // OpenIddict 权限串格式（文档化固定格式，常量名跨版本有差异故用字面量）：
        // ept:端点 / gt:授权类型 / scp:scope
        var permissions = new List<string> { "ept:token" };

        if (options.UseUserInfoEndpoint)
            permissions.Add("ept:userinfo");

        foreach (var grantType in client.GrantTypes)
            permissions.Add($"gt:{grantType}");

        foreach (var scope in client.Scopes)
            permissions.Add($"scp:{scope}");

        foreach (var permission in client.Permissions)
            permissions.Add(permission);

        return [.. permissions];
    }

    public ValueTask<long> CountAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<long>(_applications.Count);

    public ValueTask<OpenIddictApplicationDescriptor> InstantiateAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new OpenIddictApplicationDescriptor());

    public ValueTask<OpenIddictApplicationDescriptor?> FindByIdAsync(string identifier, CancellationToken cancellationToken = default) =>
        FindByClientIdAsync(identifier, cancellationToken);

    public ValueTask<OpenIddictApplicationDescriptor?> FindByClientIdAsync(string identifier, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_applications.TryGetValue(identifier, out var application) ? application : null);

    public IAsyncEnumerable<OpenIddictApplicationDescriptor> FindByRedirectUriAsync(string address, CancellationToken cancellationToken = default) =>
    Empty<OpenIddictApplicationDescriptor>();

    public IAsyncEnumerable<OpenIddictApplicationDescriptor> FindByPostLogoutRedirectUriAsync(string address, CancellationToken cancellationToken = default) =>
    Empty<OpenIddictApplicationDescriptor>();

    public ValueTask<string?> GetIdAsync(OpenIddictApplicationDescriptor application, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<string?>(application.ClientId ?? string.Empty);

    public ValueTask<string?> GetClientIdAsync(OpenIddictApplicationDescriptor application, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(application.ClientId);

    public ValueTask<string?> GetClientSecretAsync(OpenIddictApplicationDescriptor application, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(application.ClientSecret);

    public ValueTask<string?> GetClientTypeAsync(OpenIddictApplicationDescriptor application, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(application.ClientType);

    public ValueTask<string?> GetApplicationTypeAsync(OpenIddictApplicationDescriptor application, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(application.ApplicationType);

    public ValueTask<string?> GetConsentTypeAsync(OpenIddictApplicationDescriptor application, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(application.ConsentType);

    public ValueTask<string?> GetDisplayNameAsync(OpenIddictApplicationDescriptor application, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(application.DisplayName);

    public ValueTask<ImmutableDictionary<CultureInfo, string>> GetDisplayNamesAsync(OpenIddictApplicationDescriptor application, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Bag<ImmutableDictionary<CultureInfo, string>>(application, "DisplayNames"));

    public ValueTask<ImmutableArray<string>> GetPermissionsAsync(OpenIddictApplicationDescriptor application, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Bag<ImmutableArray<string>>(application, "Permissions"));

    public ValueTask<ImmutableArray<string>> GetRedirectUrisAsync(OpenIddictApplicationDescriptor application, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Bag<ImmutableArray<string>>(application, "RedirectUris"));

    public ValueTask<ImmutableArray<string>> GetPostLogoutRedirectUrisAsync(OpenIddictApplicationDescriptor application, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Bag<ImmutableArray<string>>(application, "PostLogoutRedirectUris"));

    public ValueTask<ImmutableDictionary<string, string>> GetSettingsAsync(OpenIddictApplicationDescriptor application, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Bag<ImmutableDictionary<string, string>>(application, "Settings"));

    public ValueTask<ImmutableDictionary<string, System.Text.Json.JsonElement>> GetPropertiesAsync(OpenIddictApplicationDescriptor application, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Bag<ImmutableDictionary<string, System.Text.Json.JsonElement>>(application, "Properties"));

    public ValueTask<ImmutableArray<string>> GetRequirementsAsync(OpenIddictApplicationDescriptor application, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Bag<ImmutableArray<string>>(application, "Requirements"));

    public ValueTask<JsonWebKeySet?> GetJsonWebKeySetAsync(OpenIddictApplicationDescriptor application, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(application.JsonWebKeySet);

    private static T Bag<T>(OpenIddictApplicationDescriptor application, string name) =>
        StoreBag.Get(application).TryGetValue(name, out var value) && value is T typed ? typed : default!;

    // ---------- 写路径：配置驱动，不支持 ----------

    public ValueTask CreateAsync(OpenIddictApplicationDescriptor application, CancellationToken cancellationToken = default)
    {
        _applications[application.ClientId ?? Guid.NewGuid().ToString("n")] = application;
        return ValueTask.CompletedTask;
    }

    public ValueTask UpdateAsync(OpenIddictApplicationDescriptor application, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("内存应用存储为配置驱动（只读），不支持运行时更新。");

    public ValueTask DeleteAsync(OpenIddictApplicationDescriptor application, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("内存应用存储为配置驱动（只读），不支持运行时删除。");

    public ValueTask SetClientIdAsync(OpenIddictApplicationDescriptor application, string? identifier, CancellationToken cancellationToken = default)
    {
        application.ClientId = identifier;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetClientSecretAsync(OpenIddictApplicationDescriptor application, string? identifier, CancellationToken cancellationToken = default)
    {
        application.ClientSecret = identifier;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetClientTypeAsync(OpenIddictApplicationDescriptor application, string? type, CancellationToken cancellationToken = default)
    {
        application.ClientType = type;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetApplicationTypeAsync(OpenIddictApplicationDescriptor application, string? type, CancellationToken cancellationToken = default)
    {
        application.ApplicationType = type;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetConsentTypeAsync(OpenIddictApplicationDescriptor application, string? type, CancellationToken cancellationToken = default)
    {
        application.ConsentType = type;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetDisplayNameAsync(OpenIddictApplicationDescriptor application, string? name, CancellationToken cancellationToken = default)
    {
        application.DisplayName = name;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetDisplayNamesAsync(OpenIddictApplicationDescriptor application, ImmutableDictionary<CultureInfo, string> names, CancellationToken cancellationToken = default)
    {
        StoreBag.Get(application)["DisplayNames"] = names;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetPermissionsAsync(OpenIddictApplicationDescriptor application, ImmutableArray<string> permissions, CancellationToken cancellationToken = default)
    {
        StoreBag.Get(application)["Permissions"] = permissions;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetRedirectUrisAsync(OpenIddictApplicationDescriptor application, ImmutableArray<string> addresses, CancellationToken cancellationToken = default)
    {
        StoreBag.Get(application)["RedirectUris"] = addresses;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetPostLogoutRedirectUrisAsync(OpenIddictApplicationDescriptor application, ImmutableArray<string> addresses, CancellationToken cancellationToken = default)
    {
        StoreBag.Get(application)["PostLogoutRedirectUris"] = addresses;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetSettingsAsync(OpenIddictApplicationDescriptor application, ImmutableDictionary<string, string> settings, CancellationToken cancellationToken = default)
    {
        StoreBag.Get(application)["Settings"] = settings;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetPropertiesAsync(OpenIddictApplicationDescriptor application, ImmutableDictionary<string, System.Text.Json.JsonElement> properties, CancellationToken cancellationToken = default)
    {
        StoreBag.Get(application)["Properties"] = properties;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetRequirementsAsync(OpenIddictApplicationDescriptor application, ImmutableArray<string> requirements, CancellationToken cancellationToken = default)
    {
        StoreBag.Get(application)["Requirements"] = requirements;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetJsonWebKeySetAsync(OpenIddictApplicationDescriptor application, JsonWebKeySet? set, CancellationToken cancellationToken = default)
    {
        application.JsonWebKeySet = set;
        return ValueTask.CompletedTask;
    }

    public ValueTask<long> CountAsync<TResult>(
        Func<IQueryable<OpenIddictApplicationDescriptor>, IQueryable<TResult>> query,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(query(_applications.Values.AsQueryable()).LongCount());

    public IAsyncEnumerable<OpenIddictApplicationDescriptor> ListAsync(int? count, int? offset, CancellationToken cancellationToken = default) =>
        _applications.Values.Skip(offset ?? 0).Take(count ?? int.MaxValue).ToAsyncEnumerable();

    public IAsyncEnumerable<TResult> ListAsync<TResult>(
        Func<IQueryable<OpenIddictApplicationDescriptor>, IQueryable<TResult>> query,
        CancellationToken cancellationToken = default) =>
        query(_applications.Values.AsQueryable()).ToAsyncEnumerable();

    public IAsyncEnumerable<TResult> ListAsync<TState, TResult>(
        Func<IQueryable<OpenIddictApplicationDescriptor>, TState, IQueryable<TResult>> query,
        TState state,
        CancellationToken cancellationToken = default) =>
        query(_applications.Values.AsQueryable(), state).ToAsyncEnumerable();

    private static async IAsyncEnumerable<T> Empty<T>()
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask<TResult?> GetAsync<TState, TResult>(
        Func<IQueryable<OpenIddictApplicationDescriptor>, TState, IQueryable<TResult>> query,
        TState state,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(query(_applications.Values.AsQueryable(), state).FirstOrDefault());

    public ValueTask PruneAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
