using Microsoft.EntityFrameworkCore;

namespace Ledon.Queues;

/// <summary>
/// 收件箱消息：broker 至少一次投递的去重凭据。生命周期：
/// 认领（<see cref="IInboxStore.TryClaimAsync"/>）→ 分发 → 完成 → 按保留期清理。
/// 与 <see cref="OutboxMessage"/> 同库存储（复用 <see cref="OutboxDbContext"/>）。
/// </summary>
public class InboxMessage
{
    public Guid Id { get; set; }

    /// <summary>投递标识：发件箱消息 Id 或 broker MessageId（跨服务全局唯一）。</summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>事件类型全名（诊断用）。</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>本次认领时间（超过认领超时未完成视为陈旧，可被重新认领重试）。</summary>
    public DateTime ClaimedAt { get; set; }

    /// <summary>处理完成时间（非空 = 已消费，后续同 MessageId 投递直接跳过）。</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>被认领次数（&gt;1 说明发生过崩溃重试，诊断用）。</summary>
    public int ClaimCount { get; set; }
}

/// <summary>
/// 收件箱存储抽象。替换实现即可换存储（默认 EF Core，与发件箱同库）。
/// 仅在注册了 <see cref="IInboxStore"/> 时，集成事件分发才做去重。
/// </summary>
public interface IInboxStore
{
    /// <summary>
    /// 尝试认领消息。true = 首次到达（或上次处理崩溃留下的陈旧认领），调用方应继续分发；
    /// false = 已处理完成或正在被其他实例处理，调用方应跳过（去重生效）。
    /// </summary>
    Task<bool> TryClaimAsync(string messageId, string eventType, CancellationToken cancellationToken = default);

    /// <summary>标记消息处理完成（后续同 MessageId 的重复投递将被跳过）。</summary>
    Task MarkCompletedAsync(string messageId, CancellationToken cancellationToken = default);

    /// <summary>清理超过保留期的已完成消息，返回删除条数。</summary>
    Task<int> DeleteCompletedAsync(TimeSpan retention, CancellationToken cancellationToken = default);
}

/// <summary>收件箱调度参数。</summary>
public sealed class LedonInboxOptions
{
    /// <summary>
    /// 认领超时：认领后超过此时长仍未完成，视为上次处理崩溃，允许重新认领重试（至少一次语义不被破坏）。
    /// 默认 5 分钟——应大于单条事件的最长处理时间。
    /// </summary>
    public TimeSpan ClaimTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>已完成消息保留时长，默认 7 天（按 <see cref="CleanupInterval"/> 清理）。</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>清理间隔，默认 1 小时。</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);
}
