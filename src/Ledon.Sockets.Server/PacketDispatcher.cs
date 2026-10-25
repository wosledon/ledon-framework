using Ledon.Protocols;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Sockets.Server;

/// <summary>标记协议包的类型标识（用于按类型路由到处理器）。</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class PacketTypeAttribute(ushort typeId) : Attribute
{
    /// <summary>类型标识（线上两个字节小端，由具体协议约定）。</summary>
    public ushort Id { get; } = typeId;
}

/// <summary>协议包处理器（一个类型一个处理器，由容器解析）。</summary>
public interface IPacketHandler<in TPacket>
{
    /// <summary>处理一个包。</summary>
    Task HandleAsync(ConnectionContext connection, TPacket packet, CancellationToken cancellationToken = default);
}

/// <summary>
/// 类型化分发器：帧 → 解出类型标识 → 反序列化 → 路由到 <see cref="IPacketHandler{TPacket}"/>。
/// 处理器经约定扫描自动注册（与框架其它包一致），业务只写处理器。
/// </summary>
public interface IPacketDispatcher
{
    /// <summary>分发一个帧（读类型标识 → 反序列化 → 处理器）。</summary>
    ValueTask DispatchAsync(ConnectionContext connection, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default);
}

/// <summary>
/// 默认实现：类型标识取自帧的前 2 字节（小端），其余部分按 <see cref="IPacketCodec{TPacket}"/> 反序列化。
/// 协议自定义头部时实现 <see cref="IPacketDispatcher"/> 即可。
/// </summary>
public sealed class TypeIdPacketDispatcher : IPacketDispatcher
{
    public async ValueTask DispatchAsync(ConnectionContext connection, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
    {
        if (frame.Length < 2)
            throw new ProtocolException("帧长度不足 2 字节，无法读取类型标识");

        var typeId = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(frame.Span);

        var registration = PacketHandlerRegistry.Find(typeId)
            ?? throw new ProtocolException($"未注册类型标识 {typeId} 的处理器（在包类型上加 [PacketType({typeId})]）");

        using var scope = connection.Services.CreateScope();

        var handler = scope.ServiceProvider.GetRequiredService(registration.HandlerType);
        var packet = registration.Decode(frame.Slice(2));

        await registration.InvokeAsync(handler, connection, packet, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>处理器注册项（缓存，避免每次反射）。</summary>
public sealed class PacketHandlerRegistration
{
    public required Type HandlerType { get; init; }

    public required Type PacketType { get; init; }

    public required ushort TypeId { get; init; }

    public required Func<ReadOnlyMemory<byte>, object?> Decode { get; init; }

    public required Func<object, ConnectionContext, object?, CancellationToken, Task> InvokeAsync { get; init; }
}

/// <summary>处理器注册表：启动时扫描一次，之后零反射查找。</summary>
public static class PacketHandlerRegistry
{
    private static readonly Dictionary<ushort, PacketHandlerRegistration> Handlers = new();

    /// <summary>已注册的类型标识。</summary>
    public static IReadOnlyCollection<ushort> TypeIds => Handlers.Keys.ToArray();

    /// <summary>查找处理器（未注册返回 null）。</summary>
    public static PacketHandlerRegistration? Find(ushort typeId) =>
        Handlers.TryGetValue(typeId, out var registration) ? registration : null;

    /// <summary>注册一个处理器（重复注册后者覆盖前者）。</summary>
    public static void Register(PacketHandlerRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        Handlers[registration.TypeId] = registration;
    }
}
