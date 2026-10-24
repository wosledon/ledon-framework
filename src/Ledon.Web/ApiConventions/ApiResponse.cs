using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Ledon.Web.ApiConventions;

/// <summary>
/// 统一响应包装（可选）。启用后所有成功响应包装为
/// <c>{"code":0,"data":...}</c>——前端只需处理一种信封结构。
/// 不适合纯 REST/开放 API 场景，默认关闭。
/// </summary>
public sealed class ApiResponse
{
    public int Code { get; init; }

    public string? Message { get; init; }

    public object? Data { get; init; }

    public static ApiResponse Ok(object? data) => new() { Code = 0, Data = data };

    public static ApiResponse Fail(int code, string message) => new() { Code = code, Message = message };
}

/// <summary>带数据的统一响应。</summary>
public sealed class ApiResponse<T>
{
    public ApiResponse(T? data)
    {
        Data = data;
    }

    public int Code { get; init; }

    public string? Message { get; init; }

    public T? Data { get; init; }

    public static ApiResponse<T> Ok(T data) => new(data);

    public static ApiResponse<T> Fail(int code, string message) => new(default) { Code = code, Message = message };
}

/// <summary>把 ObjectResult 包装成 <see cref="ApiResponse{T}"/> 的结果过滤器（启用统一响应时生效）。</summary>
internal sealed class ApiResponseWrapperFilter : IResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is not ObjectResult objectResult || objectResult.Value is null)
            return;

        // 已经是信封 / ProblemDetails 的不重复包装
        if (objectResult.Value is ApiResponse || objectResult.Value is ProblemDetails)
            return;

        var valueType = objectResult.Value.GetType();
        var wrapperType = typeof(ApiResponse<>).MakeGenericType(valueType);
        var wrapped = Activator.CreateInstance(wrapperType, objectResult.Value);

        context.Result = new ObjectResult(wrapped) { StatusCode = objectResult.StatusCode };
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }
}
