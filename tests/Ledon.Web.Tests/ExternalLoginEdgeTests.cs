using System.Net;
using System.Text;
using System.Text.Json;
using Ledon.Security.ExternalLogins;

namespace Ledon.Web.Tests;

/// <summary>外部登录边界：OIDC 配置/错误响应、用户映射、默认解析器。</summary>
public class ExternalLoginEdgeTests
{
    [Fact]
    public void Oidc_provider_requires_all_endpoints()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new OidcExternalLoginProvider(
            new ExternalLoginProviderOptions
            {
                Name = "oidc",
                ClientId = "id",
                ClientSecret = "secret",
            }));

        Assert.Contains("Endpoint", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oidc_provider_exchanges_code_and_reads_userinfo()
    {
        var handler = new OidcHandler();
        using var provider = new OidcExternalLoginProvider(Options(), handler);

        var user = await provider.ExchangeCodeAsync("code-1", "https://app/callback");

        Assert.Equal("oidc", user.Provider);
        Assert.Equal("subject-1", user.Subject);
        Assert.Equal("alice@example.com", user.Email);
        Assert.Equal("Alice", user.Name);
        Assert.Equal("Alice", user.Claims["name"]);
        Assert.Contains(handler.Requests, request => request.Url.Contains("token", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, request => request.Url.Contains("userinfo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Oidc_provider_rejects_failed_token_response()
    {
        var handler = new OidcHandler { TokenStatus = HttpStatusCode.BadGateway };
        using var provider = new OidcExternalLoginProvider(Options(), handler);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ExchangeCodeAsync("code-1", "https://app/callback"));
    }

    [Fact]
    public async Task Oidc_provider_rejects_userinfo_without_subject()
    {
        var handler = new OidcHandler { UserInfoBody = """{"email":"alice@example.com"}""" };
        using var provider = new OidcExternalLoginProvider(Options(), handler);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ExchangeCodeAsync("code-1", "https://app/callback"));
    }

    [Fact]
    public async Task Default_resolver_builds_stable_user_id()
    {
        var resolver = new DefaultExternalUserResolver();
        var user = await resolver.ResolveAsync(new ExternalLoginUser(
            "github", "42", "alice@example.com", null, new Dictionary<string, string>()));

        Assert.NotNull(user);
        Assert.Equal("github:42", user!.UserId);
        Assert.Equal("alice@example.com", user.UserName);
        Assert.Empty(user.Permissions);
    }

    private static ExternalLoginProviderOptions Options() => new()
    {
        Name = "oidc",
        Type = ExternalLoginProviderType.Oidc,
        ClientId = "client",
        ClientSecret = "secret",
        AuthorizationEndpoint = "https://idp/authorize",
        TokenEndpoint = "https://idp/token",
        UserInfoEndpoint = "https://idp/userinfo",
    };

    private sealed class OidcHandler : HttpMessageHandler
    {
        public HttpStatusCode TokenStatus { get; init; } = HttpStatusCode.OK;
        public string UserInfoBody { get; init; } = """{"sub":"subject-1","email":"alice@example.com","name":"Alice"}""";
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(request.RequestUri!.ToString()));

            if (request.RequestUri.AbsolutePath.Contains("token", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(TokenStatus)
                {
                    Content = new StringContent("""{"access_token":"access-1"}""", Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(UserInfoBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record CapturedRequest(string Url);
}
