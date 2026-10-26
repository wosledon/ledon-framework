using System.Net;
using System.Text;
using Ledon.Secrets;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Ledon.Web.Tests;

/// <summary>密钥边界：KV v1、404 与 Optional 配置源容错。</summary>
public class SecretEdgeTests
{
    [Fact]
    public async Task Vault_kv_v1_reads_direct_data()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal("https://vault.test/v1/secret/app/config", request.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":{"username":"bob"}}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        });

        using var provider = new VaultSecretProvider(
            new VaultOptions { Address = "https://vault.test", Token = "token", KvV2 = false },
            new HttpClient(handler));

        Assert.Equal("bob", await provider.GetSecretAsync("app/config#username"));
    }

    [Fact]
    public async Task Vault_not_found_returns_empty_secret_set()
    {
        using var provider = new VaultSecretProvider(
            new VaultOptions { Address = "https://vault.test", Token = "token" },
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))));

        Assert.Empty(await provider.GetSecretsAsync("missing"));
        Assert.Null(await provider.GetSecretAsync("missing#value"));
    }

    [Fact]
    public void Optional_configuration_source_swallows_provider_failure()
    {
        var source = new VaultConfigurationSource(
            new VaultOptions { Paths = ["app"], Optional = true },
            _ => new ThrowingSecretProvider());

        var configuration = new ConfigurationBuilder().Add(source).Build();

        Assert.Empty(configuration.AsEnumerable());
    }

    private sealed class ThrowingSecretProvider : ISecretProvider
    {
        public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("vault down");

        public Task<IReadOnlyDictionary<string, string>> GetSecretsAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("vault down");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
