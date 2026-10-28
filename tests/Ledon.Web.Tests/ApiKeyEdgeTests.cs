using Ledon.Security.ApiKeys;

namespace Ledon.Web.Tests;

/// <summary>动态 API Key 边界：无效输入、未知 Key 吊销。</summary>
public class ApiKeyEdgeTests
{
    [Fact]
    public async Task Invalid_key_is_rejected()
    {
        var service = new ApiKeyService(new InMemoryApiKeyStore(), new ApiKeyManagementOptions());

        Assert.Null(await service.ValidateAsync(""));
        Assert.Null(await service.ValidateAsync("   "));
        Assert.Null(await service.ValidateAsync("not-a-key"));
    }

    [Fact]
    public async Task Create_rejects_blank_name()
    {
        var service = new ApiKeyService(new InMemoryApiKeyStore(), new ApiKeyManagementOptions());

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync("   "));
    }

    [Fact]
    public async Task Revoke_unknown_key_returns_false()
    {
        var service = new ApiKeyService(new InMemoryApiKeyStore(), new ApiKeyManagementOptions());

        Assert.False(await service.RevokeAsync(Guid.NewGuid()));
    }
}
