using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using OpenIddict.Abstractions;

namespace Ledon.OpenIddict;

/// <summary>
/// 内存授权存储（刷新令牌流程用，完整 CRUD）。
/// 进程内有效——多实例部署换 OpenIddict 官方的 EF/Redis 存储。
/// </summary>
internal sealed class InMemoryAuthorizationStore : IOpenIddictAuthorizationStore<OpenIddictAuthorizationDescriptor>
{
    private readonly ConcurrentDictionary<string, OpenIddictAuthorizationDescriptor> _authorizations = new(StringComparer.Ordinal);

    public ValueTask<long> CountAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<long>(_authorizations.Count);

    public ValueTask<OpenIddictAuthorizationDescriptor> InstantiateAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new OpenIddictAuthorizationDescriptor());

    public ValueTask CreateAsync(OpenIddictAuthorizationDescriptor authorization, CancellationToken cancellationToken = default)
    {
        _authorizations[Guid.NewGuid().ToString("n")] = authorization;
        return ValueTask.CompletedTask;
    }

    public ValueTask UpdateAsync(OpenIddictAuthorizationDescriptor authorization, CancellationToken cancellationToken = default)
    {
        var id = GetId(authorization);
        if (id is not null)
            _authorizations[id] = authorization;

        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(OpenIddictAuthorizationDescriptor authorization, CancellationToken cancellationToken = default)
    {
        var id = GetId(authorization);
        if (id is not null)
            _authorizations.TryRemove(id, out _);

        return ValueTask.CompletedTask;
    }

    public ValueTask<OpenIddictAuthorizationDescriptor?> FindByIdAsync(string identifier, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_authorizations.TryGetValue(identifier, out var authorization) ? authorization : null);

    public IAsyncEnumerable<OpenIddictAuthorizationDescriptor> FindByApplicationIdAsync(string identifier, CancellationToken cancellationToken = default) =>
    _authorizations.Values.Where(authorization => authorization.ApplicationId == identifier).ToAsyncEnumerable();

    public IAsyncEnumerable<OpenIddictAuthorizationDescriptor> FindBySubjectAsync(string subject, CancellationToken cancellationToken = default) =>
    _authorizations.Values.Where(authorization => authorization.Subject == subject).ToAsyncEnumerable();

    public IAsyncEnumerable<OpenIddictAuthorizationDescriptor> FindAsync(
        string? subject, string? type, string? status, string? applicationId, ImmutableArray<string>? scopes, CancellationToken cancellationToken = default) =>
    _authorizations.Values.Where(authorization =>
        (subject is null || authorization.Subject == subject) &&
        (type is null || authorization.Type == type) &&
        (status is null || authorization.Status == status) &&
        (applicationId is null || authorization.ApplicationId == applicationId)).ToAsyncEnumerable();

    public ValueTask<long> RevokeAsync(string? subject, string? type, string? status, string? applicationId, CancellationToken cancellationToken = default)
    {
        foreach (var authorization in _authorizations.Values.Where(authorization =>
            (subject is null || authorization.Subject == subject) &&
            (type is null || authorization.Type == type) &&
            (applicationId is null || authorization.ApplicationId == applicationId)))
        {
            authorization.Status = OpenIddictConstants.Statuses.Revoked;
        }

        return ValueTask.FromResult<long>(0);
    }

    public ValueTask<long> RevokeByApplicationIdAsync(string identifier, CancellationToken cancellationToken = default)
    {
        foreach (var authorization in _authorizations.Values.Where(authorization => authorization.ApplicationId == identifier))
            authorization.Status = OpenIddictConstants.Statuses.Revoked;

        return ValueTask.FromResult<long>(0);
    }

    public ValueTask<long> RevokeBySubjectAsync(string subject, CancellationToken cancellationToken = default)
    {
        foreach (var authorization in _authorizations.Values.Where(authorization => authorization.Subject == subject))
            authorization.Status = OpenIddictConstants.Statuses.Revoked;

        return ValueTask.FromResult<long>(0);
    }

    public ValueTask<long> PruneAsync(DateTimeOffset minDate, CancellationToken cancellationToken = default)
    {
        var pruned = 0;
        foreach (var (id, authorization) in _authorizations)
        {
            if ((authorization.CreationDate ?? DateTimeOffset.MinValue) < minDate
                || authorization.Status is OpenIddictConstants.Statuses.Revoked or OpenIddictConstants.Statuses.Inactive)
            {
                if (_authorizations.TryRemove(id, out _))
                    pruned++;
            }
        }

        return ValueTask.FromResult<long>(pruned);
    }

    public ValueTask<string?> GetIdAsync(OpenIddictAuthorizationDescriptor authorization, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<string?>(GetId(authorization) ?? string.Empty);

    public ValueTask<string?> GetApplicationIdAsync(OpenIddictAuthorizationDescriptor authorization, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(authorization.ApplicationId);

    public ValueTask<string?> GetSubjectAsync(OpenIddictAuthorizationDescriptor authorization, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(authorization.Subject);

    public ValueTask<string?> GetTypeAsync(OpenIddictAuthorizationDescriptor authorization, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(authorization.Type);

    public ValueTask<string?> GetStatusAsync(OpenIddictAuthorizationDescriptor authorization, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(authorization.Status);

    public ValueTask<DateTimeOffset?> GetCreationDateAsync(OpenIddictAuthorizationDescriptor authorization, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(authorization.CreationDate);

    public ValueTask<ImmutableArray<string>> GetScopesAsync(OpenIddictAuthorizationDescriptor authorization, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Bag<ImmutableArray<string>>(authorization, "Scopes"));

    public ValueTask<ImmutableDictionary<string, JsonElement>> GetPropertiesAsync(OpenIddictAuthorizationDescriptor authorization, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Bag<ImmutableDictionary<string, JsonElement>>(authorization, "Properties"));

    public ValueTask<ClaimsPrincipal?> GetPrincipalAsync(OpenIddictAuthorizationDescriptor authorization, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(authorization.Principal);

    private string? GetId(OpenIddictAuthorizationDescriptor authorization) =>
        _authorizations.FirstOrDefault(pair => ReferenceEquals(pair.Value, authorization)).Key;

    private static T Bag<T>(OpenIddictAuthorizationDescriptor authorization, string name) =>
        StoreBag.Get(authorization).TryGetValue(name, out var value) && value is T typed ? typed : StoreBag.Empty<T>();

    public ValueTask SetApplicationIdAsync(OpenIddictAuthorizationDescriptor authorization, string? identifier, CancellationToken cancellationToken = default)
    {
        authorization.ApplicationId = identifier;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetSubjectAsync(OpenIddictAuthorizationDescriptor authorization, string? subject, CancellationToken cancellationToken = default)
    {
        authorization.Subject = subject;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetTypeAsync(OpenIddictAuthorizationDescriptor authorization, string? type, CancellationToken cancellationToken = default)
    {
        authorization.Type = type;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetStatusAsync(OpenIddictAuthorizationDescriptor authorization, string? status, CancellationToken cancellationToken = default)
    {
        authorization.Status = status;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetCreationDateAsync(OpenIddictAuthorizationDescriptor authorization, DateTimeOffset? date, CancellationToken cancellationToken = default)
    {
        authorization.CreationDate = date;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetScopesAsync(OpenIddictAuthorizationDescriptor authorization, ImmutableArray<string> scopes, CancellationToken cancellationToken = default)
    {
        StoreBag.Get(authorization)["Scopes"] = scopes;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetPropertiesAsync(OpenIddictAuthorizationDescriptor authorization, ImmutableDictionary<string, JsonElement> properties, CancellationToken cancellationToken = default)
    {
        StoreBag.Get(authorization)["Properties"] = properties;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetPrincipalAsync(OpenIddictAuthorizationDescriptor authorization, ClaimsPrincipal? principal, CancellationToken cancellationToken = default)
    {
        authorization.Principal = principal;
        return ValueTask.CompletedTask;
    }

    public ValueTask<long> CountAsync<TResult>(
        Func<IQueryable<OpenIddictAuthorizationDescriptor>, IQueryable<TResult>> query,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(query(_authorizations.Values.AsQueryable()).LongCount());

    public IAsyncEnumerable<OpenIddictAuthorizationDescriptor> ListAsync(int? count, int? offset, CancellationToken cancellationToken = default) =>
        _authorizations.Values.Skip(offset ?? 0).Take(count ?? int.MaxValue).ToAsyncEnumerable();

    public IAsyncEnumerable<TResult> ListAsync<TResult>(
        Func<IQueryable<OpenIddictAuthorizationDescriptor>, IQueryable<TResult>> query,
        CancellationToken cancellationToken = default) =>
        query(_authorizations.Values.AsQueryable()).ToAsyncEnumerable();

    public IAsyncEnumerable<TResult> ListAsync<TState, TResult>(
        Func<IQueryable<OpenIddictAuthorizationDescriptor>, TState, IQueryable<TResult>> query,
        TState state,
        CancellationToken cancellationToken = default) =>
        query(_authorizations.Values.AsQueryable(), state).ToAsyncEnumerable();

    public ValueTask<TResult?> GetAsync<TState, TResult>(
        Func<IQueryable<OpenIddictAuthorizationDescriptor>, TState, IQueryable<TResult>> query,
        TState state,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(query(_authorizations.Values.AsQueryable(), state).FirstOrDefault());
}
