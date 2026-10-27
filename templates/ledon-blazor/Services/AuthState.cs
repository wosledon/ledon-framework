namespace Ledon.BlazorApp.Services;

public sealed class AuthState
{
    public string? AccessToken { get; set; }

    public string? UserName { get; set; }

    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(AccessToken);
}
