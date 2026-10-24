namespace Ledon.Web;

/// <summary>Web 功能开关。默认只开"控制器自动发现"，其余按需开启。</summary>
public sealed class LedonWebOptions
{
    /// <summary>自动发现控制器（默认开）：扫描到的业务程序集会挂为 MVC ApplicationPart。</summary>
    public bool UseControllers { get; set; } = true;

    /// <summary>启用 OpenAPI 文档（默认关，按需开启；基于 .NET 10 内置 AddOpenApi/MapOpenApi）。</summary>
    public bool UseOpenApi { get; set; }

    /// <summary>OpenAPI 文档路径。</summary>
    public string OpenApiPath { get; set; } = "/openapi/v1.json";
}
