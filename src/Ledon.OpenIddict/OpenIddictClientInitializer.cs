using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenIddict.Abstractions;

namespace Ledon.OpenIddict;

/// <summary>
/// 客户端初始化器（宿主启动时运行）：把配置里的客户端定义经 <see cref="IOpenIddictApplicationManager"/>
/// 创建进应用存储——客户端密钥由此走 OpenIddict 的哈希约定（管理器内部处理）。
/// 幂等：已存在的 client_id 跳过；多实例同时启动时唯一索引冲突视为成功。
/// </summary>
internal sealed class OpenIddictClientInitializer : IHostedService
{
    private readonly IServiceProvider _provider;
    private readonly LedonOpenIddictOptions _options;

    public OpenIddictClientInitializer(IServiceProvider provider, LedonOpenIddictOptions options)
    {
        _provider = provider;
        _options = options;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = _provider.CreateAsyncScope();
        var services = scope.ServiceProvider;

        // EF 模式的 scope 不会像内存存储那样自动播种，需经管理器落库
        if (_options.Storage == OpenIddictStorage.EntityFrameworkCore)
        {
            var scopeManager = services.GetRequiredService<IOpenIddictScopeManager>();

            foreach (var definition in _options.Scopes)
            {
                if (await scopeManager.FindByNameAsync(definition.Name, cancellationToken) is not null)
                    continue;

                var descriptor = new OpenIddictScopeDescriptor
                {
                    Name = definition.Name,
                    DisplayName = definition.DisplayName,
                    Description = definition.DisplayName,
                };

                try
                {
                    await scopeManager.CreateAsync(descriptor, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    if (await scopeManager.FindByNameAsync(definition.Name, cancellationToken) is null)
                        throw;
                }
            }
        }

        var manager = services.GetRequiredService<IOpenIddictApplicationManager>();

        foreach (var client in _options.Clients)
        {
            if (string.IsNullOrEmpty(client.ClientId))
                throw new InvalidOperationException("OpenIddict 客户端定义缺少 ClientId");

            if (await manager.FindByClientIdAsync(client.ClientId, cancellationToken) is not null)
                continue;

            var descriptor = new OpenIddictApplicationDescriptor
            {
                ClientId = client.ClientId,
                DisplayName = client.DisplayName,
                ClientType = string.IsNullOrEmpty(client.ClientSecret) ? "public" : "confidential",
                ApplicationType = "web",
                ConsentType = "explicit",
            };

            descriptor.Permissions.UnionWith(InMemoryApplicationStore.BuildPermissions(_options, client));

            try
            {
                await CreateApplicationAsync(services, manager, descriptor, client.ClientSecret, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 多实例同时启动：另一个实例已创建同名客户端（唯一索引冲突），视为成功
                if (await manager.FindByClientIdAsync(client.ClientId, cancellationToken) is null)
                    throw;
            }
        }
    }

    /// <summary>
    /// 用管理器当前实体类型创建应用：内存/EF 的实体类型不同（Descriptor vs OpenIddictApplication），
    /// 通过 InstantiateAsync + PopulateAsync 走同一套官方填充流程，避免把 Descriptor 强转成 EF 实体。
    /// </summary>
    private static async Task CreateApplicationAsync(
        IServiceProvider services,
        IOpenIddictApplicationManager manager,
        OpenIddictApplicationDescriptor descriptor,
        string? secret,
        CancellationToken cancellationToken)
    {
        var managerType = manager.GetType();
        var genericManager = managerType;

        while (genericManager is not null
               && !(genericManager.IsGenericType
                   && genericManager.GetGenericTypeDefinition() == typeof(global::OpenIddict.Core.OpenIddictApplicationManager<>)))
        {
            genericManager = genericManager.BaseType;
        }

        if (genericManager is null)
            throw new InvalidOperationException("无法识别 OpenIddictApplicationManager 的实体类型");

        var entityType = genericManager.GetGenericArguments()[0];
        var storeType = typeof(IOpenIddictApplicationStore<>).MakeGenericType(entityType);
        var store = services.GetRequiredService(storeType);

        var pending = storeType.GetMethod("InstantiateAsync")!.Invoke(store, [cancellationToken])!;
        var task = (Task)pending.GetType().GetMethod("AsTask")!.Invoke(pending, null)!;
        await task.ConfigureAwait(false);

        var entity = task.GetType().GetProperty("Result")!.GetValue(task)!;

        // 方向：entity ← descriptor（object 在前）；PopulateAsync 会写入 clientId/密钥/权限等全部属性
        await manager.PopulateAsync(entity, descriptor, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(secret))
            await manager.CreateAsync(entity, secret, cancellationToken).ConfigureAwait(false);
        else
            await manager.CreateAsync(entity, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
