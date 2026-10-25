using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ledon.Notifications.Feishu;

/// <summary>飞书事件回调信封（解密后的结构）。</summary>
public sealed class FeishuEvent
{
    /// <summary>事件头。</summary>
    [JsonPropertyName("header")]
    public FeishuEventHeader? Header { get; set; }

    /// <summary>事件体（按事件类型不同而不同，原样保留）。</summary>
    [JsonPropertyName("event")]
    public JsonElement Event { get; set; }

    /// <summary>schema 2.0 的 challenge（URL 校验时使用）。</summary>
    [JsonPropertyName("challenge")]
    public string? Challenge { get; set; }

    /// <summary>URL 校验令牌（开启校验时与 <see cref="FeishuOptions.VerificationToken"/> 比对）。</summary>
    [JsonPropertyName("token")]
    public string? Token { get; set; }

    /// <summary>事件类型（如 <c>im.message.receive_v1</c>）。</summary>
    public string? EventType => Header?.EventType;

    /// <summary>租户 key。</summary>
    public string? TenantKey => Header?.TenantKey;
}

/// <summary>事件头。</summary>
public sealed class FeishuEventHeader
{
    [JsonPropertyName("event_id")]
    public string? EventId { get; set; }

    [JsonPropertyName("event_type")]
    public string? EventType { get; set; }

    [JsonPropertyName("create_time")]
    public string? CreateTime { get; set; }

    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("app_id")]
    public string? AppId { get; set; }

    [JsonPropertyName("tenant_key")]
    public string? TenantKey { get; set; }
}

/// <summary>消息接收事件（<c>im.message.receive_v1</c>）的常用字段。</summary>
public sealed class FeishuMessageEvent
{
    [JsonPropertyName("sender")]
    public FeishuSender? Sender { get; set; }

    [JsonPropertyName("message")]
    public FeishuMessage? Message { get; set; }
}

/// <summary>发送者。</summary>
public sealed class FeishuSender
{
    [JsonPropertyName("sender_id")]
    public FeishuUserId? SenderId { get; set; }

    [JsonPropertyName("sender_type")]
    public string? SenderType { get; set; }

    [JsonPropertyName("tenant_key")]
    public string? TenantKey { get; set; }
}

/// <summary>用户标识集合。</summary>
public sealed class FeishuUserId
{
    [JsonPropertyName("union_id")]
    public string? UnionId { get; set; }

    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }

    [JsonPropertyName("open_id")]
    public string? OpenId { get; set; }
}

/// <summary>消息体。</summary>
public sealed class FeishuMessage
{
    [JsonPropertyName("message_id")]
    public string? MessageId { get; set; }

    [JsonPropertyName("root_id")]
    public string? RootId { get; set; }

    [JsonPropertyName("parent_id")]
    public string? ParentId { get; set; }

    [JsonPropertyName("chat_id")]
    public string? ChatId { get; set; }

    [JsonPropertyName("chat_type")]
    public string? ChatType { get; set; }

    [JsonPropertyName("message_type")]
    public string? MessageType { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("create_time")]
    public string? CreateTime { get; set; }
}

/// <summary>飞书事件处理器：实现后由事件回调端点自动分发。</summary>
public interface IFeishuEventHandler<in TEvent> where TEvent : class
{
    /// <summary>处理事件。抛出异常只影响本次回调（飞书会重推），不影响其他处理器。</summary>
    Task HandleAsync(TEvent @event, CancellationToken cancellationToken = default);
}

/// <summary>信封级处理器（按事件类型自行分发，最灵活）。</summary>
public interface IFeishuEventHandler : IFeishuEventHandler<FeishuEvent>;
