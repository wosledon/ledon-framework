using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Ledon.BlazorApp.Services;

public sealed class AuthApi(HttpClient http, AuthState state)
{
    public sealed record LoginRequest(string UserName, string Password);

    public sealed record TokenResponse(string AccessToken, string RefreshToken, int ExpiresInSeconds);

    public sealed record MeResponse(string UserId, string[] Roles, string[] Permissions, bool IsAdmin);

    public async Task<bool> LoginAsync(string userName, string password, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(
            "/auth/login",
            new LoginRequest(userName, password),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
            return false;

        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);

        if (token is null)
            return false;

        state.AccessToken = token.AccessToken;
        state.UserName = userName;
        return true;
    }

    public async Task<MeResponse?> GetMeAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/authorization/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", state.AccessToken);

        using var response = await http.SendAsync(request, cancellationToken);

        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<MeResponse>(cancellationToken)
            : null;
    }

    public async Task<string?> CallWeatherAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/weather");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", state.AccessToken);

        using var response = await http.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadAsStringAsync(cancellationToken)
            : $"HTTP {(int)response.StatusCode}";
    }
}
