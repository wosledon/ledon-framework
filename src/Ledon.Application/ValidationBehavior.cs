using Microsoft.Extensions.Logging;

namespace Ledon.Application;

/// <summary>命令/查询自校验约定：实现该接口即在管线最外层先校验（失败直接 400，不进处理器）。</summary>
public interface IValidatable
{
    /// <summary>校验自身，返回全部错误（空 = 通过）。</summary>
    IReadOnlyCollection<string> Validate();
}

/// <summary>
/// 验证行为：对实现 <see cref="IValidatable"/> 的请求先校验，失败抛
/// <see cref="ValidationException"/>（Web 层可映射为 400）。不进处理器、不碰数据库。
/// </summary>
public sealed class ValidationBehavior<TRequest>(ILogger<ValidationBehavior<TRequest>> logger) : IPipelineBehavior<TRequest>
    where TRequest : notnull
{
    public ValueTask<object?> HandleAsync(
        TRequest request, RequestHandlerDelegate next, CancellationToken cancellationToken = default)
    {
        if (request is not IValidatable validatable)
            return next();

        var errors = validatable.Validate();

        if (errors.Count == 0)
            return next();

        logger.LogInformation("请求 {Request} 校验失败：{Errors}", typeof(TRequest).Name, string.Join("; ", errors));
        throw new ValidationException(errors);
    }
}

/// <summary>校验失败异常（携带全部错误）。</summary>
public sealed class ValidationException(IReadOnlyCollection<string> errors)
    : Exception($"请求校验失败：{string.Join("; ", errors)}")
{
    /// <summary>全部校验错误。</summary>
    public IReadOnlyCollection<string> Errors { get; } = errors;
}
