using System.Net;
using System.Text;
using Ledon.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ledon.Web.Tests;

/// <summary>密钥：Vault KV v2 解析、缓存、配置源与内存实现。</summary>
public class SecretTests
{
    [Fact]
    public async Task Vault_reads_kv_v2_field_and_adds_token_header()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal("https://vault.test/v1/secret/data/app/config", request.RequestUri!.ToString());
            Assert.Equal("vault-token", request.Headers.GetValues("X-Vault-Token").Single());

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":{"data":{"username":"alice","password":"s3cret"}}}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        });

        using var provider = new VaultSecretProvider(
            new VaultOptions { Address = "https://vault.test", Token = "vault-token" },
            new HttpClient(handler));

        Assert.Equal("s3cret", await provider.GetSecretAsync("app/config#password"));
        Assert.Equal("alice", await provider.GetSecretAsync("app/config#username"));
        Assert.Null(await provider.GetSecretAsync("app/config#missing"));
    }

    [Fact]
    public async Task Cache_reuses_value_until_ttl_expires()
    {
        var inner = new CountingSecretProvider("value");
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var cache = new CachingSecretProvider(inner, time, new VaultOptions { CacheDuration = TimeSpan.FromMinutes(1) });

        Assert.Equal("value", await cache.GetSecretAsync("db#password"));
        Assert.Equal("value", await cache.GetSecretAsync("db#password"));
        Assert.Equal(1, inner.Calls);

        time.Now = time.Now.AddMinutes(2);
        Assert.Equal("value", await cache.GetSecretAsync("db#password"));
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public void Configuration_source_maps_vault_fields_to_keys()
    {
        var provider = new DictionarySecretProvider(new Dictionary<string, string>
        {
            ["username"] = "alice",
            ["password"] = "s3cret",
        });

        var source = new VaultConfigurationSource(
            new VaultOptions { Paths = ["app"], KeyPrefix = "Vault:" },
            _ => provider);

        var configuration = new ConfigurationBuilder()
            .Add(source)
            .Build();

        Assert.Equal("alice", configuration["Vault:username"]);
        Assert.Equal("s3cret", configuration["Vault:password"]);
    }

    [Fact]
    public async Task In_memory_provider_and_di_registration_work()
    {
        var services = new ServiceCollection();
        services.AddLedonInMemorySecrets([new KeyValuePair<string, string>("token", "abc")]);

        using var provider = services.BuildServiceProvider();
        var secrets = provider.GetRequiredService<ISecretProvider>();

        Assert.Equal("abc", await secrets.GetRequiredSecretAsync("token"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => secrets.GetRequiredSecretAsync("missing"));
    }

    private sealed class DictionarySecretProvider(IReadOnlyDictionary<string, string> values) : ISecretProvider
    {
        public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(values.TryGetValue(name, out var value) ? value : null);

        public Task<IReadOnlyDictionary<string, string>> GetSecretsAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(values);
    }

    private sealed class CountingSecretProvider(string value) : ISecretProvider
    {
        public int Calls { get; private set; }

        public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<string?>(value);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
