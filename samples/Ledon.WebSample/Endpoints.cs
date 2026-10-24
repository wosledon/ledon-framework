using Ledon.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Ledon.WebSample;

/// <summary>端点实现类即被自动发现并映射，不需要在 Program.cs 里 MapGet。</summary>
public class HelloEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder builder)
        => builder.MapGet("/hello", () => "Hello, Ledon!");
}
