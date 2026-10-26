using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using Ledon.OpenIddict;
using Ledon.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ledon.Web.Tests;

/// <summary>
/// OpenIddict 多实例：两个宿主进程共享同一个 EF 数据库、Data Protection 密钥环与签名证书，
/// 一个实例签发的令牌/刷新令牌另一个实例能直接使用。
/// </summary>
public class OpenIddictMultiInstanceTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ledon-oidc-multi-{Guid.NewGuid():N}");
    private readonly List<WebApplication> _apps = [];

    private string DatabasePath => Path.Combine(_directory, "oidc.db");
    private string KeyRingPath => Path.Combine(_directory, "keys");
    private string CertificatePath => Path.Combine(_directory, "signing.pfx");

    private const string CertificatePassword = "ledon-test";

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(KeyRingPath);

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=ledon-multi-instance",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));

        File.WriteAllBytes(CertificatePath, certificate.Export(X509ContentType.Pfx, CertificatePassword));

        return Task.CompletedTask;
    }

    private async Task<WebApplication> StartInstanceAsync(bool createDatabase)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddLedonWeb();

        // 多实例共享的 Data Protection 密钥环（刷新令牌保护用）
        builder.Services
            .AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(KeyRingPath))
            .SetApplicationName("ledon-openiddict-multi-instance-tests");

        builder.Services.AddDbContext<LedonOpenIddictDbContext>(options =>
            options.UseSqlite($"Data Source={DatabasePath};Pooling=False"));

        builder.AddLedonOpenIddict(options =>
        {
            options.Storage = OpenIddictStorage.EntityFrameworkCore;
            options.EnsureDatabaseCreated = createDatabase;
            options.SigningCertificatePath = CertificatePath;
            options.SigningCertificatePassword = CertificatePassword;
            options.Clients.Add(new OpenIddictClientDefinition
            {
                ClientId = "webapp",
                ClientSecret = "webapp-secret",
                DisplayName = "Web 客户端",
                GrantTypes = ["password", "refresh_token"],
                Scopes = ["openid", "profile", "offline_access"],
            });
        });

        var app = builder.Build();
        app.UseLedonWeb();
        await app.StartAsync();

        _apps.Add(app);
        return app;
    }

    [Fact]
    public async Task Token_issued_by_one_instance_is_usable_by_another()
    {
        var first = await StartInstanceAsync(createDatabase: true);
        var second = await StartInstanceAsync(createDatabase: false);

        var firstClient = first.GetTestClient();
        var login = await firstClient.PostAsync("/connect/token", Form(new()
        {
            ["client_id"] = "webapp",
            ["client_secret"] = "webapp-secret",
            ["grant_type"] = "password",
            ["username"] = "alice",
            ["password"] = "password",
            ["scope"] = "openid profile offline_access",
        }));

        Assert.True(
            login.StatusCode == HttpStatusCode.OK,
            $"第一个实例登录失败：{(int)login.StatusCode} {await login.Content.ReadAsStringAsync()}");

        var tokens = await login.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(tokens);
        Assert.False(string.IsNullOrWhiteSpace(tokens.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(tokens.RefreshToken));

        var secondClient = second.GetTestClient();

        // 第二个实例从共享库读取刷新令牌并轮换（Data Protection 密钥环共享）
        var refreshed = await secondClient.PostAsync("/connect/token", Form(new()
        {
            ["client_id"] = "webapp",
            ["client_secret"] = "webapp-secret",
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.RefreshToken!,
        }));

        Assert.True(
            refreshed.StatusCode == HttpStatusCode.OK,
            $"第二个实例刷新失败：{(int)refreshed.StatusCode} {await refreshed.Content.ReadAsStringAsync()}");

        var rotated = await refreshed.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(rotated);
        Assert.NotEqual(tokens.RefreshToken, rotated.RefreshToken);

        // 第二个实例用共享签名证书校验第一个实例签发的访问令牌
        var userinfo = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        userinfo.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var response = await secondClient.SendAsync(userinfo);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static FormUrlEncodedContent Form(Dictionary<string, string> values) => new(values);

    public async Task DisposeAsync()
    {
        foreach (var app in _apps)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 测试清理失败不掩盖用例结果
        }
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("token_type")] string TokenType);
}
