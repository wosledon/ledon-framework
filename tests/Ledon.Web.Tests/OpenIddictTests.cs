using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Ledon.OpenIddict;
using Ledon.Web;
using Ledon.Web.Tests.Fixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ledon.Web.Tests;

/// <summary>OpenIddict OIDC 服务器端到端测试：password / refresh 流程 + 令牌校验。</summary>
public class OpenIddictTests : IDisposable
{
    private WebApplication? _app;

    private async Task<HttpClient> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddLedonWeb();
        builder.AddLedonOpenIddict(options =>
        {
            options.Clients.Add(new OpenIddictClientDefinition
            {
                ClientId = "webapp",
                ClientSecret = "webapp-secret",
                DisplayName = "Web 客户端",
                GrantTypes = ["password", "refresh_token", "client_credentials"],
                Scopes = ["openid", "profile", "offline_access"],
            });
        });

        var app = builder.Build();
        app.UseLedonWeb();
        _app = app;
        await app.StartAsync();
        return app.GetTestClient();
    }

    private static FormUrlEncodedContent TokenRequest(params (string Key, string? Value)[] parameters)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = "webapp",
            ["client_secret"] = "webapp-secret",
        };

        foreach (var (key, value) in parameters)
            form[key] = value ?? string.Empty;

        return new FormUrlEncodedContent(form);
    }

    [Fact]
    public async Task Password_grant_issues_token_pair()
    {
        var client = await StartAsync();

        var response = await client.PostAsync("/connect/token", TokenRequest(
            ("grant_type", "password"),
            ("username", "alice"),
            ("password", "password"),
            ("scope", "openid profile offline_access")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(tokens);
        Assert.False(string.IsNullOrWhiteSpace(tokens.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(tokens.RefreshToken));
        Assert.Equal("Bearer", tokens.TokenType);
    }

    [Fact]
    public async Task Wrong_password_is_rejected()
    {
        var client = await StartAsync();

        var response = await client.PostAsync("/connect/token", TokenRequest(
            ("grant_type", "password"),
            ("username", "alice"),
            ("password", "wrong")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_grant", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Refresh_token_rotates()
    {
        var client = await StartAsync();

        var login = await client.PostAsync("/connect/token", TokenRequest(
            ("grant_type", "password"),
            ("username", "alice"),
            ("password", "password"),
            ("scope", "openid profile offline_access")));

        var tokens = await login.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(tokens);

        var refreshed = await client.PostAsync("/connect/token", TokenRequest(
            ("grant_type", "refresh_token"),
            ("refresh_token", tokens.RefreshToken)));

        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);

        var rotated = await refreshed.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(rotated);
        Assert.NotEqual(tokens.RefreshToken, rotated.RefreshToken);

        // 新刷新令牌可继续使用
        var again = await client.PostAsync("/connect/token", TokenRequest(
            ("grant_type", "refresh_token"),
            ("refresh_token", rotated.RefreshToken)));

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task Client_credentials_grant_issues_token()
    {
        var client = await StartAsync();

        var response = await client.PostAsync("/connect/token", TokenRequest(
            ("grant_type", "client_credentials"),
            ("scope", "openid")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(tokens);
        Assert.False(string.IsNullOrWhiteSpace(tokens.AccessToken));
    }

    [Fact]
    public async Task Access_token_is_a_jwt_with_identity_claims()
    {
        var client = await StartAsync();

        var login = await client.PostAsync("/connect/token", TokenRequest(
            ("grant_type", "password"),
            ("username", "alice"),
            ("password", "password"),
            ("scope", "openid profile offline_access")));

        var tokens = await login.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(tokens);

        // 访问令牌是携带身份声明的 JWT（sub/name/权限可本地解码验证）
        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler
        {
            MapInboundClaims = false,
        };

        var jwt = handler.ReadJwtToken(tokens.AccessToken);
        Assert.Equal("u1", jwt.Claims.First(claim => claim.Type == "sub").Value);
        Assert.Equal("alice", jwt.Claims.First(claim => claim.Type == "name").Value);
        Assert.Contains(jwt.Claims, claim => claim.Type == "permission" && claim.Value == "secure.read");
    }
    public void Dispose()
    {
        if (_app is not null)
        {
            _app.StopAsync().GetAwaiter().GetResult();
            _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Duplicate_client_ids_fail_fast()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddLedonWeb();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.AddLedonOpenIddict(options =>
            {
                options.Clients.Add(new OpenIddictClientDefinition { ClientId = "dup" });
                options.Clients.Add(new OpenIddictClientDefinition { ClientId = "dup" });
            }));

        Assert.Contains("ClientId", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Relative_issuer_fails_fast()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddLedonWeb();

        Assert.Throws<InvalidOperationException>(() =>
            builder.AddLedonOpenIddict(options =>
            {
                options.Issuer = "not-an-absolute-uri";
                options.Clients.Add(new OpenIddictClientDefinition { ClientId = "webapp" });
            }));
    }

    [Fact]
    public void Missing_signing_certificate_fails_fast()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddLedonWeb();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.AddLedonOpenIddict(options =>
            {
                options.SigningCertificatePath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.pfx");
                options.Clients.Add(new OpenIddictClientDefinition { ClientId = "webapp" });
            }));

        Assert.Contains("证书不存在", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Signing_certificate_is_loaded_when_configured()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ledon-oidc-{Guid.NewGuid():N}.pfx");

        try
        {
            using (var rsa = System.Security.Cryptography.RSA.Create(2048))
            {
                var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
                    "CN=ledon-test",
                    rsa,
                    System.Security.Cryptography.HashAlgorithmName.SHA256,
                    System.Security.Cryptography.RSASignaturePadding.Pkcs1);

                using var certificate = request.CreateSelfSigned(
                    DateTimeOffset.UtcNow.AddDays(-1),
                    DateTimeOffset.UtcNow.AddDays(1));

                File.WriteAllBytes(
                    path,
                    certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, "pwd"));
            }

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.AddLedonWeb();

            builder.AddLedonOpenIddict(options =>
            {
                options.SigningCertificatePath = path;
                options.SigningCertificatePassword = "pwd";
                options.Clients.Add(new OpenIddictClientDefinition
                {
                    ClientId = "webapp",
                    GrantTypes = ["password"],
                });
            });

            var app = builder.Build();
            app.UseLedonWeb();

            // 证书能加载并完成装配（启动细节由其余端到端用例覆盖）
            Assert.NotNull(app);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("token_type")] string TokenType);
}
