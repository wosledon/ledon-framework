using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Application;

/// <summary>管线行为登记项（哪个行为、适用哪些请求、顺序）。</summary>
public sealed class BehaviorRegistration(Type behaviorType, Func<Type, bool> appliesTo, int order)
{
    /// <summary>行为实现类型（须实现 <see cref="IPipelineBehavior{TRequest}"/>）。</summary>
    public Type BehaviorType { get; } = behaviorType;

    /// <summary>该行为是否适用于指定请求类型。</summary>
    public bool AppliesTo(Type requestType) => appliesTo(requestType);

    /// <summary>顺序（小的在外层，先执行）。</summary>
    public int Order { get; } = order;

    /// <summary>登记一个适用于全部请求的行为。</summary>
    public static BehaviorRegistration All<TBehavior>(int order = 0) where TBehavior : notnull =>
        new(typeof(TBehavior), _ => true, order);

    /// <summary>登记一个只适用于指定请求类型的行为。</summary>
    public static BehaviorRegistration For<TRequest, TBehavior>(int order = 0)
        where TRequest : notnull
        where TBehavior : notnull =>
        new(typeof(TBehavior), requestType => typeof(TRequest).IsAssignableFrom(requestType), order);
}

/// <summary>应用层选项。</summary>
public sealed class LedonApplicationOptions
{
    /// <summary>已登记的管线行为（按顺序，外层先执行）。</summary>
    public IList<BehaviorRegistration> Behaviors { get; } = [];

    /// <summary>登记行为。</summary>
    public LedonApplicationOptions AddBehavior(BehaviorRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        Behaviors.Add(registration);
        return this;
    }

    /// <summary>便捷登记：日志行为（全部请求）。</summary>
    public LedonApplicationOptions UseLogging() =>
        AddBehavior(new BehaviorRegistration(typeof(LoggingBehavior<object>), _ => true, Behaviors.Count));

    /// <summary>便捷登记：校验行为（全部请求，对实现 <see cref="IValidatable"/> 的请求生效）。</summary>
    public LedonApplicationOptions UseValidation() =>
        AddBehavior(new BehaviorRegistration(typeof(ValidationBehavior<object>), _ => true, Behaviors.Count));

    /// <summary>便捷登记：事务行为（全部命令；未注册 IUnitOfWork 时直通）。</summary>
    public LedonApplicationOptions UseTransactions() =>
        AddBehavior(new BehaviorRegistration(typeof(TransactionBehavior<object>), _ => true, Behaviors.Count));

    /// <summary>便捷登记：缓存行为（全部查询，对实现 <see cref="ICacheableQuery"/> 的查询生效）。</summary>
    public LedonApplicationOptions UseCaching() =>
        AddBehavior(new BehaviorRegistration(typeof(CachingBehavior<object>), _ => true, Behaviors.Count));
}

/// <summary>
/// 应用层统一入口：命令/查询经管线分发到处理器。
/// 处理器只写业务，横切关注点（验证、日志、事务、缓存、幂等）由行为按登记顺序织入：
/// <code>
/// public class CreateOrderHandler(IUnitOfWork uow) : ICommandHandler&lt;CreateOrder, Guid&gt; { ... }
///
/// // 调用方（端点/控制器）
/// var id = await sender.SendAsync(new CreateOrder(cmd), ct);
/// </code>
/// </summary>
public interface IRequestSender
{
    /// <summary>发送命令。</summary>
    ValueTask<TResponse> SendAsync<TResponse>(
        ICommand<TResponse> request, CancellationToken cancellationToken = default);

    /// <summary>发送无返回值的命令。</summary>
    ValueTask SendAsync(ICommand request, CancellationToken cancellationToken = default);

    /// <summary>发送查询。</summary>
    ValueTask<TResponse> QueryAsync<TResponse>(
        IQuery<TResponse> query, CancellationToken cancellationToken = default);
}

/// <summary>默认实现：解析处理器 + 按登记顺序穿管线。</summary>
public sealed class RequestSender : IRequestSender
{
    // 处理器/行为的反射调用句柄缓存（启动后不再变，避免每次查方法）
    private static readonly ConcurrentDictionary<Type, Func<object, object, CancellationToken, Task<object?>>> HandlerCache = new();
    private static readonly ConcurrentDictionary<Type, Func<object, object, RequestHandlerDelegate, CancellationToken, ValueTask<object?>>> BehaviorCache = new();

    private readonly IServiceProvider _services;
    private readonly LedonApplicationOptions _options;

    public RequestSender(IServiceProvider services, LedonApplicationOptions options)
    {
        _services = services;
        _options = options;
    }

    public ValueTask<TResponse> SendAsync<TResponse>(
        ICommand<TResponse> request, CancellationToken cancellationToken = default) =>
        DispatchAsync<TResponse>(request, request.GetType(), cancellationToken);

    public async ValueTask SendAsync(ICommand request, CancellationToken cancellationToken = default) =>
        await DispatchAsync<object>(request, request.GetType(), cancellationToken).ConfigureAwait(false);

    public ValueTask<TResponse> QueryAsync<TResponse>(
        IQuery<TResponse> query, CancellationToken cancellationToken = default) =>
        DispatchAsync<TResponse>(query, query.GetType(), cancellationToken);

    [RequiresDynamicCode("命令/查询分发基于反射调用处理器（处理器类型是运行时发现的）。")]
    private async ValueTask<TResponse> DispatchAsync<TResponse>(
        object request, Type requestType, CancellationToken cancellationToken)
    {
        var handlerType = HandlerInterfaceFor(requestType)
            ?? throw new InvalidOperationException(
                $"未找到 {requestType.Name} 的处理器（应实现 ICommandHandler<,> 或 IQueryHandler<,>）");

        var handler = _services.GetService(handlerType)
            ?? throw new InvalidOperationException($"处理器 {handlerType.Name} 未注册（约定扫描会自动注册）");

        var invoke = HandlerCache.GetOrAdd(handlerType, BuildInvoker);

        RequestHandlerDelegate next = async () => await invoke(handler, request, cancellationToken).ConfigureAwait(false);

        foreach (var registration in ApplicableBehaviors(requestType).Reverse())
        {
            var behavior = _services.GetRequiredService(registration.BehaviorType);
            var behaviorInvoke = BehaviorCache.GetOrAdd(registration.BehaviorType, BuildBehaviorInvoker);
            var captured = next;

            next = () => behaviorInvoke(behavior, request, captured, cancellationToken);
        }

        var result = await next().ConfigureAwait(false);

        return (TResponse)result!;
    }

    private IEnumerable<BehaviorRegistration> ApplicableBehaviors(Type requestType) =>
        _options.Behaviors
            .Where(registration => registration.AppliesTo(requestType))
            .OrderBy(registration => registration.Order);

    /// <summary>
    /// 从请求类型推导处理器接口：<c>ICommand&lt;T&gt;</c> → <c>ICommandHandler&lt;TRequest, T&gt;</c>，
    /// <c>IQuery&lt;T&gt;</c> → <c>IQueryHandler&lt;TRequest, T&gt;</c>；无返回值命令按 <c>object</c> 响应处理。
    /// </summary>
    private static Type? HandlerInterfaceFor(Type requestType)
    {
        foreach (var candidate in requestType.GetInterfaces())
        {
            if (!candidate.IsGenericType)
                continue;

            var definition = candidate.GetGenericTypeDefinition();

            if (definition == typeof(ICommand<>) || definition == typeof(IQuery<>))
            {
                var responseType = candidate.GetGenericArguments()[0];
                var handler = definition == typeof(ICommand<>)
                    ? typeof(ICommandHandler<,>)
                    : typeof(IQueryHandler<,>);

                return handler.MakeGenericType(requestType, responseType);
            }
        }

        // 无返回值命令（ICommand）：处理器实现 ICommandHandler<TRequest>（单泛型）
        if (typeof(ICommand).IsAssignableFrom(requestType))
            return typeof(ICommandHandler<>).MakeGenericType(requestType);

        return null;
    }

    private static Func<object, object, CancellationToken, Task<object?>> BuildInvoker(Type handlerType)
    {
        var method = handlerType.GetMethod(nameof(ICommandHandler<ICommand<object>, object>.HandleAsync))
            ?? throw new InvalidOperationException($"处理器 {handlerType.Name} 缺少 HandleAsync 方法");

        return async (target, request, ct) =>
        {
            Task task;
            try
            {
                task = (Task)method.Invoke(target, [request, ct])!;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }

            await task.ConfigureAwait(false);

            // Task<T> 的 Result 经反射取出（Task 本身 Result 为 void，取不到即 null）
            return task.GetType().GetProperty("Result")?.GetValue(task);
        };
    }

    private static Func<object, object, RequestHandlerDelegate, CancellationToken, ValueTask<object?>> BuildBehaviorInvoker(Type behaviorType)
    {
        var method = behaviorType.GetMethod(nameof(IPipelineBehavior<ICommand>.HandleAsync))
            ?? throw new InvalidOperationException($"行为 {behaviorType.Name} 缺少 HandleAsync 方法");

        return (target, request, next, ct) =>
        {
            try
            {
                return (ValueTask<object?>)method.Invoke(target, [request, next, ct])!;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }
        };
    }
}
