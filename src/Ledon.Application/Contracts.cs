namespace Ledon.Application;

/// <summary>无返回值的命令。</summary>
public interface ICommand;

/// <summary>有返回值的命令。</summary>
/// <typeparam name="TResponse">响应类型。</typeparam>
public interface ICommand<out TResponse>;

/// <summary>查询。</summary>
/// <typeparam name="TResponse">响应类型。</typeparam>
public interface IQuery<out TResponse>;

/// <summary>
/// 无返回值命令的处理器（响应即 <c>null</c>，调用方用 <see cref="IRequestSender.SendAsync(ICommand, CancellationToken)"/>）。
/// </summary>
public interface ICommandHandler<in TCommand> where TCommand : ICommand
{
    /// <summary>处理命令。</summary>
    Task HandleAsync(TCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
/// 命令/查询处理器。与具体类型一一对应，由容器解析（约定扫描自动注册）：
/// <code>
/// public class CreateOrderHandler(IUnitOfWork uow) : ICommandHandler&lt;CreateOrder, Guid&gt;
/// {
///     public async Task&lt;Guid&gt; HandleAsync(CreateOrder command, CancellationToken ct) { ... }
/// }
/// </code>
/// </summary>
public interface ICommandHandler<in TCommand, TResponse> where TCommand : ICommand<TResponse>
{
    /// <summary>处理命令。</summary>
    Task<TResponse> HandleAsync(TCommand command, CancellationToken cancellationToken = default);
}

/// <summary>查询处理器。</summary>
public interface IQueryHandler<in TQuery, TResponse> where TQuery : IQuery<TResponse>
{
    /// <summary>处理查询。</summary>
    Task<TResponse> HandleAsync(TQuery query, CancellationToken cancellationToken = default);
}

/// <summary>管线中的下一步（由行为调用；返回值即处理器的响应）。</summary>
public delegate ValueTask<object?> RequestHandlerDelegate();

/// <summary>
/// 管线行为：在处理器前后织入横切关注点（验证/日志/事务/缓存/幂等）。
/// 响应在线内经 <see cref="object"/> 传递，端点侧仍拿强类型结果。
/// </summary>
/// <typeparam name="TRequest">命令或查询。</typeparam>
public interface IPipelineBehavior<in TRequest> where TRequest : notnull
{
    /// <summary>执行行为：可前置处理、调用 <paramref name="next"/>、后置处理。</summary>
    ValueTask<object?> HandleAsync(
        TRequest request,
        RequestHandlerDelegate next,
        CancellationToken cancellationToken = default);
}
