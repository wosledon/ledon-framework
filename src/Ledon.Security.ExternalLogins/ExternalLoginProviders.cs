using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Ledon.Security.ExternalLogins;

/// <summary>标准 OIDC 授权码流程。</summary>
public sealed class OidcExternalLoginProvider : IExternalLoginProvider, IDisposable
{
    private readonly ExternalLoginProviderOptions _options;
    private readonly HttpClient _http;

    public OidcExternalLoginProvider(ExternalLoginProviderOptions options, HttpMessageHandler? handler = null)
    {
        _options = options;

        if (string.IsNullOrWhiteSpace(options.AuthorizationEndpoint)
            || string.IsNullOrWhiteSpace(options.TokenEndpoint)
            || string.IsNullOrWhiteSpace(options.UserInfoEndpoint))
        {
            throw new InvalidOperationException($"OIDC 提供程序 {options.Name} 需要配置 AuthorizationEndpoint/TokenEndpoint/UserInfoEndpoint");
        }

        _http = new HttpClient(handler ?? new SocketsHttpHandler(), disposeHandler: handler is null);
    }

    public string Name => _options.Name;

    public string BuildAuthorizationUrl(string state, string redirectUri)
    {
        var scopes = _options.Scopes is { Length: > 0 }
            ? string.Join(' ', _options.Scopes)
            : "openid profile email";

        return QueryHelpers.AddQueryString(_options.AuthorizationEndpoint!, new Dictionary<string, string?>
        {
            ["client_id"] = _options.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = scopes,
            ["state"] = state,
        });
    }

    public async Task<ExternalLoginUser> ExchangeCodeAsync(
        string code,
        string redirectUri,
        CancellationToken cancellationToken = default)
    {
        using var tokenResponse = await _http.PostAsync(_options.TokenEndpoint, new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret,
            }), cancellationToken).ConfigureAwait(false);

        var tokenPayload = await tokenResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!tokenResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"OIDC token 端点返回 HTTP {(int)tokenResponse.StatusCode}: {tokenPayload}");

        using var tokenDocument = JsonDocument.Parse(tokenPayload);
        var accessToken = tokenDocument.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("OIDC token 响应缺少 access_token");

        using var request = new HttpRequestMessage(HttpMethod.Get, _options.UserInfoEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var userResponse = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var userPayload = await userResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!userResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"OIDC userinfo 返回 HTTP {(int)userResponse.StatusCode}: {userPayload}");

        using var userDocument = JsonDocument.Parse(userPayload);
        var root = userDocument.RootElement;
        var subject = root.TryGetProperty("sub", out var sub) ? sub.GetString() : null;

        if (string.IsNullOrEmpty(subject))
            throw new InvalidOperationException("OIDC userinfo 缺少 sub");

        var claims = root.EnumerateObject()
            .Where(property => property.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(property => property.Name, property => property.Value.GetString() ?? "", StringComparer.Ordinal);

        return new ExternalLoginUser(
            Name,
            subject!,
            root.TryGetProperty("email", out var email) ? email.GetString() : null,
            root.TryGetProperty("name", out var name) ? name.GetString() : null,
            claims);
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>GitHub OAuth App 登录。</summary>
public sealed class GitHubExternalLoginProvider : IExternalLoginProvider, IDisposable
{
    private const string AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
    private const string TokenEndpoint = "https://github.com/login/oauth/access_token";
    private const string UserEndpoint = "https://api.github.com/user";
    private const string EmailsEndpoint = "https://api.github.com/user/emails";

    private readonly ExternalLoginProviderOptions _options;
    private readonly HttpClient _http;

    public GitHubExternalLoginProvider(ExternalLoginProviderOptions options, HttpMessageHandler? handler = null)
    {
        _options = options;
        _http = new HttpClient(handler ?? new SocketsHttpHandler(), disposeHandler: handler is null);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Ledon", "1.0"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public string Name => _options.Name;

    public string BuildAuthorizationUrl(string state, string redirectUri)
    {
        var scopes = _options.Scopes is { Length: > 0 }
            ? string.Join(' ', _options.Scopes)
            : "read:user user:email";

        return QueryHelpers.AddQueryString(AuthorizationEndpoint, new Dictionary<string, string?>
        {
            ["client_id"] = _options.ClientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = scopes,
            ["state"] = state,
        });
    }

    public async Task<ExternalLoginUser> ExchangeCodeAsync(
        string code,
        string redirectUri,
        CancellationToken cancellationToken = default)
    {
        using var tokenResponse = await _http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret,
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
            }), cancellationToken).ConfigureAwait(false);

        var tokenPayload = await tokenResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!tokenResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub token 端点返回 HTTP {(int)tokenResponse.StatusCode}: {tokenPayload}");

        using var tokenDocument = JsonDocument.Parse(tokenPayload);
        var accessToken = tokenDocument.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("GitHub token 响应缺少 access_token");

        using var userRequest = new HttpRequestMessage(HttpMethod.Get, UserEndpoint);
        userRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var userResponse = await _http.SendAsync(userRequest, cancellationToken).ConfigureAwait(false);
        var userPayload = await userResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!userResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub user 端点返回 HTTP {(int)userResponse.StatusCode}: {userPayload}");

        using var userDocument = JsonDocument.Parse(userPayload);
        var user = userDocument.RootElement;

        if (!user.TryGetProperty("id", out var id))
            throw new InvalidOperationException("GitHub user 响应缺少 id");

        var subject = id.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var login = user.TryGetProperty("login", out var loginValue) ? loginValue.GetString() : null;
        var name = user.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
        var email = user.TryGetProperty("email", out var emailValue) ? emailValue.GetString() : null;

        if (string.IsNullOrEmpty(email))
            email = await TryGetPrimaryEmailAsync(accessToken, cancellationToken).ConfigureAwait(false);

        var claims = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["login"] = login ?? "",
            ["name"] = name ?? "",
            ["email"] = email ?? "",
        };

        return new ExternalLoginUser(Name, subject, email, name ?? login, claims);
    }

    public void Dispose() => _http.Dispose();

    private async Task<string?> TryGetPrimaryEmailAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, EmailsEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return null;

        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in document.RootElement.EnumerateArray())
        {
            var primary = item.TryGetProperty("primary", out var primaryValue) && primaryValue.GetBoolean();
            var verified = item.TryGetProperty("verified", out var verifiedValue) && verifiedValue.GetBoolean();

            if (primary && verified && item.TryGetProperty("email", out var emailValue))
                return emailValue.GetString();
        }

        return null;
    }
}
