using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using OpenIddict.Abstractions;

namespace Ledon.OpenIddict;

/// <summary>
/// 内存令牌存储（刷新令牌流程用，完整 CRUD）。
/// 进程内有效——多实例部署换 OpenIddict 官方的 EF/Redis 存储。
/// </summary>
internal sealed class InMemoryTokenStore : IOpenIddictTokenStore<OpenIddictTokenDescriptor>
{
    private readonly ConcurrentDictionary<string, OpenIddictTokenDescriptor> _tokens = new(StringComparer.Ordinal);

    public ValueTask<long> CountAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<long>(_tokens.Count);

    public ValueTask<OpenIddictTokenDescriptor> InstantiateAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new OpenIddictTokenDescriptor());

    public ValueTask CreateAsync(OpenIddictTokenDescriptor token, CancellationToken cancellationToken = default)
    {
        _tokens[Guid.NewGuid().ToString("n")] = token;
        return ValueTask.CompletedTask;
    }

    public ValueTask UpdateAsync(OpenIddictTokenDescriptor token, CancellationToken cancellationToken = default)
    {
        var id = GetId(token);
        if (id is not null)
            _tokens[id] = token;

        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(OpenIddictTokenDescriptor token, CancellationToken cancellationToken = default)
    {
        var id = GetId(token);
        if (id is not null)
            _tokens.TryRemove(id, out _);

        return ValueTask.CompletedTask;
    }

    public ValueTask<OpenIddictTokenDescriptor?> FindByIdAsync(string identifier, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_tokens.TryGetValue(identifier, out var token) ? token : null);

    public ValueTask<OpenIddictTokenDescriptor?> FindByReferenceIdAsync(string identifier, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_tokens.Values.FirstOrDefault(token => token.ReferenceId == identifier));

    public IAsyncEnumerable<OpenIddictTokenDescriptor> FindByAuthorizationIdAsync(string identifier, CancellationToken cancellationToken = default) =>
    _tokens.Values.Where(token => token.AuthorizationId == identifier).ToAsyncEnumerable();

    public IAsyncEnumerable<OpenIddictTokenDescriptor> FindByApplicationIdAsync(string identifier, CancellationToken cancellationToken = default) =>
    _tokens.Values.Where(token => token.ApplicationId == identifier).ToAsyncEnumerable();

    public IAsyncEnumerable<OpenIddictTokenDescriptor> FindBySubjectAsync(string subject, CancellationToken cancellationToken = default) =>
    _tokens.Values.Where(token => token.Subject == subject).ToAsyncEnumerable();

    public IAsyncEnumerable<OpenIddictTokenDescriptor> FindAsync(
        string? subject, string? type, string? status, string? applicationId, CancellationToken cancellationToken = default) =>
    _tokens.Values.Where(token =>
        (subject is null || token.Subject == subject) &&
        (type is null || token.Type == type) &&
        (status is null || token.Status == status) &&
        (applicationId is null || token.ApplicationId == applicationId)).ToAsyncEnumerable();

    public ValueTask<long> RevokeAsync(string? subject, string? type, string? status, string? applicationId, CancellationToken cancellationToken = default)
    {
        foreach (var token in _tokens.Values.Where(token =>
            (subject is null || token.Subject == subject) &&
            (type is null || token.Type == type) &&
            (applicationId is null || token.ApplicationId == applicationId)))
        {
            token.Status = OpenIddictConstants.Statuses.Revoked;
        }

        return ValueTask.FromResult<long>(0);
    }

    public ValueTask<long> RevokeByApplicationIdAsync(string identifier, CancellationToken cancellationToken = default)
    {
        foreach (var token in _tokens.Values.Where(token => token.ApplicationId == identifier))
            token.Status = OpenIddictConstants.Statuses.Revoked;

        return ValueTask.FromResult<long>(0);
    }

    public ValueTask<long> RevokeByAuthorizationIdAsync(string identifier, CancellationToken cancellationToken = default)
    {
        foreach (var token in _tokens.Values.Where(token => token.AuthorizationId == identifier))
            token.Status = OpenIddictConstants.Statuses.Revoked;

        return ValueTask.FromResult<long>(0);
    }

    public ValueTask<long> RevokeBySubjectAsync(string subject, CancellationToken cancellationToken = default)
    {
        foreach (var token in _tokens.Values.Where(token => token.Subject == subject))
            token.Status = OpenIddictConstants.Statuses.Revoked;

        return ValueTask.FromResult<long>(0);
    }

    public ValueTask<long> PruneAsync(DateTimeOffset minDate, CancellationToken cancellationToken = default)
    {
        var pruned = 0;
        foreach (var (id, token) in _tokens)
        {
            if ((token.ExpirationDate ?? DateTimeOffset.MaxValue) < minDate
                || token.Status is OpenIddictConstants.Statuses.Revoked or OpenIddictConstants.Statuses.Inactive)
            {
                if (_tokens.TryRemove(id, out _))
                    pruned++;
            }
        }

        return ValueTask.FromResult<long>(pruned);
    }

    public ValueTask<string?> GetIdAsync(OpenIddictTokenDescriptor token, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<string?>(GetId(token) ?? string.Empty);

    public ValueTask<string?> GetReferenceIdAsync(OpenIddictTokenDescriptor token, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(token.ReferenceId);

    public ValueTask<string?> GetAuthorizationIdAsync(OpenIddictTokenDescriptor token, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(token.AuthorizationId);

    public ValueTask<string?> GetApplicationIdAsync(OpenIddictTokenDescriptor token, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(token.ApplicationId);

    public ValueTask<string?> GetSubjectAsync(OpenIddictTokenDescriptor token, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(token.Subject);

    public ValueTask<string?> GetTypeAsync(OpenIddictTokenDescriptor token, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(token.Type);

    public ValueTask<string?> GetStatusAsync(OpenIddictTokenDescriptor token, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(token.Status);

    public ValueTask<DateTimeOffset?> GetCreationDateAsync(OpenIddictTokenDescriptor token, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(token.CreationDate);

    public ValueTask<DateTimeOffset?> GetExpirationDateAsync(OpenIddictTokenDescriptor token, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(token.ExpirationDate);

    public ValueTask<DateTimeOffset?> GetRedemptionDateAsync(OpenIddictTokenDescriptor token, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(token.RedemptionDate);

    public ValueTask<string?> GetPayloadAsync(OpenIddictTokenDescriptor token, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(token.Payload);

    public ValueTask<ImmutableDictionary<string, JsonElement>> GetPropertiesAsync(OpenIddictTokenDescriptor token, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Bag<ImmutableDictionary<string, JsonElement>>(token, "Properties"));

    public ValueTask<ClaimsPrincipal?> GetPrincipalAsync(OpenIddictTokenDescriptor token, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(token.Principal);

    private string? GetId(OpenIddictTokenDescriptor token) =>
        _tokens.FirstOrDefault(pair => ReferenceEquals(pair.Value, token)).Key;

    private static T Bag<T>(OpenIddictTokenDescriptor token, string name) =>
        StoreBag.Get(token).TryGetValue(name, out var value) && value is T typed ? typed : default!;

    public ValueTask SetReferenceIdAsync(OpenIddictTokenDescriptor token, string? identifier, CancellationToken cancellationToken = default)
    {
        token.ReferenceId = identifier;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetAuthorizationIdAsync(OpenIddictTokenDescriptor token, string? identifier, CancellationToken cancellationToken = default)
    {
        token.AuthorizationId = identifier;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetApplicationIdAsync(OpenIddictTokenDescriptor token, string? identifier, CancellationToken cancellationToken = default)
    {
        token.ApplicationId = identifier;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetSubjectAsync(OpenIddictTokenDescriptor token, string? subject, CancellationToken cancellationToken = default)
    {
        token.Subject = subject;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetTypeAsync(OpenIddictTokenDescriptor token, string? type, CancellationToken cancellationToken = default)
    {
        token.Type = type;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetStatusAsync(OpenIddictTokenDescriptor token, string? status, CancellationToken cancellationToken = default)
    {
        token.Status = status;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetCreationDateAsync(OpenIddictTokenDescriptor token, DateTimeOffset? date, CancellationToken cancellationToken = default)
    {
        token.CreationDate = date;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetExpirationDateAsync(OpenIddictTokenDescriptor token, DateTimeOffset? date, CancellationToken cancellationToken = default)
    {
        token.ExpirationDate = date;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetRedemptionDateAsync(OpenIddictTokenDescriptor token, DateTimeOffset? date, CancellationToken cancellationToken = default)
    {
        token.RedemptionDate = date;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetPayloadAsync(OpenIddictTokenDescriptor token, string? payload, CancellationToken cancellationToken = default)
    {
        token.Payload = payload;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetPropertiesAsync(OpenIddictTokenDescriptor token, ImmutableDictionary<string, JsonElement> properties, CancellationToken cancellationToken = default)
    {
        StoreBag.Get(token)["Properties"] = properties;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetPrincipalAsync(OpenIddictTokenDescriptor token, ClaimsPrincipal? principal, CancellationToken cancellationToken = default)
    {
        token.Principal = principal;
        return ValueTask.CompletedTask;
    }

    public ValueTask<long> CountAsync<TResult>(
        Func<IQueryable<OpenIddictTokenDescriptor>, IQueryable<TResult>> query,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(query(_tokens.Values.AsQueryable()).LongCount());

    public IAsyncEnumerable<OpenIddictTokenDescriptor> ListAsync(int? count, int? offset, CancellationToken cancellationToken = default) =>
        _tokens.Values.Skip(offset ?? 0).Take(count ?? int.MaxValue).ToAsyncEnumerable();

    public IAsyncEnumerable<TResult> ListAsync<TResult>(
        Func<IQueryable<OpenIddictTokenDescriptor>, IQueryable<TResult>> query,
        CancellationToken cancellationToken = default) =>
        query(_tokens.Values.AsQueryable()).ToAsyncEnumerable();

    public IAsyncEnumerable<TResult> ListAsync<TState, TResult>(
        Func<IQueryable<OpenIddictTokenDescriptor>, TState, IQueryable<TResult>> query,
        TState state,
        CancellationToken cancellationToken = default) =>
        query(_tokens.Values.AsQueryable(), state).ToAsyncEnumerable();

    public ValueTask<TResult?> GetAsync<TState, TResult>(
        Func<IQueryable<OpenIddictTokenDescriptor>, TState, IQueryable<TResult>> query,
        TState state,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(query(_tokens.Values.AsQueryable(), state).FirstOrDefault());
}
