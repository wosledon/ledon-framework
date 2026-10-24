namespace Ledon.Web;

/// <summary>限流分区键用的请求头名（与 <c>Ledon.Security</c> 的 API Key 约定一致：X-Api-Key）。</summary>
internal static class RateLimiting
{
    public const string ApiKeyHeaderName = "X-Api-Key";
}
