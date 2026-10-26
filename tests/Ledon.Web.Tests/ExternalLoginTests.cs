using System.Net;
using System.Text;
using System.Text.Json;
using Ledon.Security.ExternalLogins;
using Ledon.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ledon.Web.Tests;

/// <summary>外部登录：GitHub challenge/callback、state 校验与框架令牌签发。</summary>
public class ExternalLoginTests : IAsyncDisposable
{
    private const string SigningKey = "test-signing-key-at-least-32-characters-long!!";

    private readonly FakeGitHubHandler _github = new();
    private WebApplication? _app;

    private async Task<HttpClient> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddLedonWeb();

        builder.Services.AddLedonSecurity(o => o.Jwt.SigningKey = SigningKey);
        builder.Services.AddLedonExternalLogins(o =>
        {
            o.HttpMessageHandler = _github;
            o.Providers.Add(new ExternalLoginProviderOptions
            {
                Name = "github",
                Type = ExternalLoginProviderType.GitHub,
                ClientId = "client-id",
                ClientSecret = "client-secret",
            });
        });

        var app = builder.Build();
        app.UseLedonWeb();
        await app.StartAsync();
        _app = app;

        return app.GetTestClient();
    }

    [Fact]
    public async Task Challenge_redirects_to_provider_with_state()
    {
        var client = await StartAsync();

        var response = await client.GetAsync("/auth/external/github/challenge?returnUrl=/home");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();

        Assert.StartsWith("https://github.com/login/oauth/authorize", location);
        var query = QueryHelpers.ParseQuery(new Uri(location).Query);
        Assert.Equal("client-id", query["client_id"]);
        Assert.Equal("http://localhost/auth/external/github/callback", query["redirect_uri"]);
        Assert.False(string.IsNullOrEmpty(query["state"]));
    }

    [Fact]
    public async Task Callback_exchanges_code_and_issues_framework_tokens()
    {
        var client = await StartAsync();
        var state = await GetStateAsync(client);

        var response = await client.GetAsync(
            $"/auth/external/github/callback?code=code-1&state={Uri.EscapeDataString(state)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrEmpty(document.RootElement.GetProperty("accessToken").GetString()));
        Assert.False(string.IsNullOrEmpty(document.RootElement.GetProperty("refreshToken").GetString()));

        Assert.Contains(_github.Requests, request => request.Url.Contains("/login/oauth/access_token"));
        Assert.Contains(_github.Requests, request => request.Url == "https://api.github.com/user");
    }

    [Fact]
    public async Task Callback_rejects_tampered_state()
    {
        var client = await StartAsync();

        var response = await client.GetAsync("/auth/external/github/callback?code=code-1&state=tampered");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_github.Requests);
    }

    [Fact]
    public async Task Unknown_provider_returns_404()
    {
        var client = await StartAsync();

        var response = await client.GetAsync("/auth/external/unknown/challenge");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<string> GetStateAsync(HttpClient client)
    {
        var challenge = await client.GetAsync("/auth/external/github/challenge");
        var location = challenge.Headers.Location!.ToString();

        return QueryHelpers.ParseQuery(new Uri(location).Query)["state"]!;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }

    private sealed class FakeGitHubHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add(new CapturedRequest(url, body));

            if (url.Contains("/login/oauth/access_token", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"access_token":"gho_1","token_type":"bearer"}""",
                        Encoding.UTF8, "application/json"),
                };
            }

            if (url == "https://api.github.com/user")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"id":123,"login":"alice","name":"Alice","email":"alice@example.com"}""",
                        Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed record CapturedRequest(string Url, string Body);
}
