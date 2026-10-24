using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenIddict.Abstractions;

namespace Ledon.OpenIddict;

/// <summary>
/// 客户端初始化器（宿主启动时运行）：把配置里的客户端定义经 <see cref="IOpenIddictApplicationManager"/>
/// 创建进应用存储——客户端密钥由此走 OpenIddict 的哈希约定（管理器内部处理）。
/// 幂等：已存在的 client_id 跳过。
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

        var manager = services.GetRequiredService<IOpenIddictApplicationManager>();
        var store = services.GetRequiredService<IOpenIddictApplicationStore<OpenIddictApplicationDescriptor>>();

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

            // 先设置只读属性（CreateAsync 内部会校验，缺失会空引用）
            await store.SetPermissionsAsync(
                descriptor, InMemoryApplicationStore.BuildPermissions(_options, client), cancellationToken);
            await store.SetRequirementsAsync(descriptor, [], cancellationToken);
            await store.SetRedirectUrisAsync(descriptor, [], cancellationToken);
            await store.SetPostLogoutRedirectUrisAsync(descriptor, [], cancellationToken);
            await store.SetSettingsAsync(descriptor, ImmutableDictionary<string, string>.Empty, cancellationToken);
            await store.SetPropertiesAsync(descriptor, ImmutableDictionary<string, JsonElement>.Empty, cancellationToken);

            // 机密客户端：密钥经管理器创建（内部哈希）；公共客户端直接创建
            if (!string.IsNullOrEmpty(client.ClientSecret))
                await manager.CreateAsync(descriptor, client.ClientSecret, cancellationToken);
            else
                await manager.CreateAsync(descriptor, cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
