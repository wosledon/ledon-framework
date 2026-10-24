using Microsoft.AspNetCore.Routing;

namespace Ledon.Web;

/// <summary>
/// 最小 API 端点抽象。实现类会被自动发现，并在启动时统一映射，
/// 不需要在 Program.cs 里手工 <c>MapGet/MapGroup</c>。
/// </summary>
public interface IEndpoint
{
    void Map(IEndpointRouteBuilder builder);
}
