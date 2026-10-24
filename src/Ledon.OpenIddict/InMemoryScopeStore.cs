using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using OpenIddict.Abstractions;

namespace Ledon.OpenIddict;

/// <summary>内存 scope 存储（配置驱动、只读）。</summary>
internal sealed class InMemoryScopeStore : IOpenIddictScopeStore<OpenIddictScopeDescriptor>
{
    private readonly ConcurrentDictionary<string, OpenIddictScopeDescriptor> _scopes = new(StringComparer.Ordinal);

    public InMemoryScopeStore(LedonOpenIddictOptions options)
    {
        foreach (var scope in options.Scopes)
        {
            var entry = new OpenIddictScopeDescriptor
            {
                Name = scope.Name,
                DisplayName = scope.DisplayName,
                Description = scope.DisplayName,
            };

            var bag = StoreBag.Get(entry);
            bag["Descriptions"] = ImmutableDictionary<CultureInfo, string>.Empty;
            bag["DisplayNames"] = ImmutableDictionary<CultureInfo, string>.Empty;
            bag["Resources"] = ImmutableArray<string>.Empty;
            bag["Properties"] = ImmutableDictionary<string, JsonElement>.Empty;

            _scopes[scope.Name] = entry;
        }
    }

    public ValueTask<long> CountAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<long>(_scopes.Count);

    public ValueTask<OpenIddictScopeDescriptor> InstantiateAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new OpenIddictScopeDescriptor());

    public ValueTask<OpenIddictScopeDescriptor?> FindByIdAsync(string identifier, CancellationToken cancellationToken = default) =>
        FindByNameAsync(identifier, cancellationToken);

    public ValueTask<OpenIddictScopeDescriptor?> FindByNameAsync(string identifier, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_scopes.TryGetValue(identifier, out var scope) ? scope : null);

    public IAsyncEnumerable<OpenIddictScopeDescriptor> FindByNamesAsync(ImmutableArray<string> names, CancellationToken cancellationToken = default) =>
    _scopes.Values.Where(scope => scope.Name is not null && names.Contains(scope.Name)).ToAsyncEnumerable();

    public IAsyncEnumerable<OpenIddictScopeDescriptor> FindByResourceAsync(string resource, CancellationToken cancellationToken = default) =>
    _scopes.Values.Where(scope => Bag<ImmutableArray<string>>(scope, "Resources").Contains(resource)).ToAsyncEnumerable();

    public ValueTask<string?> GetIdAsync(OpenIddictScopeDescriptor scope, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<string?>(scope.Name ?? string.Empty);

    public ValueTask<string?> GetNameAsync(OpenIddictScopeDescriptor scope, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(scope.Name);

    public ValueTask<string?> GetDisplayNameAsync(OpenIddictScopeDescriptor scope, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(scope.DisplayName);

    public ValueTask<string?> GetDescriptionAsync(OpenIddictScopeDescriptor scope, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(scope.Description);

    public ValueTask<ImmutableDictionary<CultureInfo, string>> GetDescriptionsAsync(OpenIddictScopeDescriptor scope, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Bag<ImmutableDictionary<CultureInfo, string>>(scope, "Descriptions"));

    public ValueTask<ImmutableDictionary<CultureInfo, string>> GetDisplayNamesAsync(OpenIddictScopeDescriptor scope, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Bag<ImmutableDictionary<CultureInfo, string>>(scope, "DisplayNames"));

    public ValueTask<ImmutableArray<string>> GetResourcesAsync(OpenIddictScopeDescriptor scope, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Bag<ImmutableArray<string>>(scope, "Resources"));

    public ValueTask<ImmutableDictionary<string, JsonElement>> GetPropertiesAsync(OpenIddictScopeDescriptor scope, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Bag<ImmutableDictionary<string, JsonElement>>(scope, "Properties"));

    private static T Bag<T>(OpenIddictScopeDescriptor scope, string name) =>
        StoreBag.Get(scope).TryGetValue(name, out var value) && value is T typed ? typed : default!;

    // ---------- 写路径：配置驱动，不支持 ----------

    public ValueTask CreateAsync(OpenIddictScopeDescriptor scope, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("内存 scope 存储为配置驱动（只读），不支持运行时创建。");

    public ValueTask UpdateAsync(OpenIddictScopeDescriptor scope, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("内存 scope 存储为配置驱动（只读），不支持运行时更新。");

    public ValueTask DeleteAsync(OpenIddictScopeDescriptor scope, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("内存 scope 存储为配置驱动（只读），不支持运行时删除。");

    public ValueTask SetNameAsync(OpenIddictScopeDescriptor scope, string? name, CancellationToken cancellationToken = default)
    {
        scope.Name = name;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetDisplayNameAsync(OpenIddictScopeDescriptor scope, string? name, CancellationToken cancellationToken = default)
    {
        scope.DisplayName = name;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetDescriptionAsync(OpenIddictScopeDescriptor scope, string? description, CancellationToken cancellationToken = default)
    {
        scope.Description = description;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetDescriptionsAsync(OpenIddictScopeDescriptor scope, ImmutableDictionary<CultureInfo, string> descriptions, CancellationToken cancellationToken = default)
    {
        StoreBag.Get(scope)["Descriptions"] = descriptions;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetDisplayNamesAsync(OpenIddictScopeDescriptor scope, ImmutableDictionary<CultureInfo, string> names, CancellationToken cancellationToken = default)
    {
        StoreBag.Get(scope)["DisplayNames"] = names;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetResourcesAsync(OpenIddictScopeDescriptor scope, ImmutableArray<string> resources, CancellationToken cancellationToken = default)
    {
        StoreBag.Get(scope)["Resources"] = resources;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetPropertiesAsync(OpenIddictScopeDescriptor scope, ImmutableDictionary<string, JsonElement> properties, CancellationToken cancellationToken = default)
    {
        StoreBag.Get(scope)["Properties"] = properties;
        return ValueTask.CompletedTask;
    }

    public ValueTask<long> CountAsync<TResult>(
        Func<IQueryable<OpenIddictScopeDescriptor>, IQueryable<TResult>> query,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(query(_scopes.Values.AsQueryable()).LongCount());

    public IAsyncEnumerable<OpenIddictScopeDescriptor> ListAsync(int? count, int? offset, CancellationToken cancellationToken = default) =>
        _scopes.Values.Skip(offset ?? 0).Take(count ?? int.MaxValue).ToAsyncEnumerable();

    public IAsyncEnumerable<TResult> ListAsync<TResult>(
        Func<IQueryable<OpenIddictScopeDescriptor>, IQueryable<TResult>> query,
        CancellationToken cancellationToken = default) =>
        query(_scopes.Values.AsQueryable()).ToAsyncEnumerable();

    public IAsyncEnumerable<TResult> ListAsync<TState, TResult>(
        Func<IQueryable<OpenIddictScopeDescriptor>, TState, IQueryable<TResult>> query,
        TState state,
        CancellationToken cancellationToken = default) =>
        query(_scopes.Values.AsQueryable(), state).ToAsyncEnumerable();

    public ValueTask<TResult?> GetAsync<TState, TResult>(
        Func<IQueryable<OpenIddictScopeDescriptor>, TState, IQueryable<TResult>> query,
        TState state,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(query(_scopes.Values.AsQueryable(), state).FirstOrDefault());

    public ValueTask PruneAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
