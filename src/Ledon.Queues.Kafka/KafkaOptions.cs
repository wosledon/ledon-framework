namespace Ledon.Queues.Kafka;

/// <summary>Kafka 传输配置。</summary>
public sealed class KafkaOptions
{
    /// <summary>broker 地址（逗号分隔多个），如 localhost:9092。</summary>
    public string BootstrapServers { get; set; } = "localhost:9092";

    /// <summary>
    /// 主题前缀：完整主题 = 前缀 + 事件类型全名（如 <c>ledon.MyApp.OrderPlaced</c>）。
    /// 默认 <c>ledon.</c>。发布侧与消费侧必须一致。
    /// </summary>
    public string TopicPrefix { get; set; } = "ledon.";

    /// <summary>
    /// 消费组 ID。多实例同组 = 竞争消费（每条消息只被组内一个实例处理）；
    /// 为空时取服务名（环境变量 LEDON_SERVICE_NAME，其次入口程序集名）。
    /// </summary>
    public string? GroupId { get; set; }

    /// <summary>无已提交偏移量时从何处开始消费，默认 Earliest（不丢消息）。</summary>
    public AutoOffsetReset AutoOffsetReset { get; set; } = AutoOffsetReset.Earliest;

    /// <summary>自动提交偏移量（默认开）。关掉则靠业务侧确认后手动提交。</summary>
    public bool EnableAutoCommit { get; set; } = true;

    /// <summary>自动 offset 存储的提交间隔，默认 5 秒。</summary>
    public TimeSpan AutoCommitInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>是否只消费自己发布的事件类型（默认开：订阅扫描到的集成事件类型对应的主题）。</summary>
    public bool SubscribeDiscoveredEventTypes { get; set; } = true;

    /// <summary>额外订阅的主题（发现机制之外手动指定），默认空。</summary>
    public List<string> ExtraTopics { get; set; } = [];
}

/// <summary>无已提交偏移量时的消费起点。</summary>
public enum AutoOffsetReset
{
    /// <summary>从最早消息开始（不丢消息）。</summary>
    Earliest,

    /// <summary>从最新消息开始（重启后只收新消息）。</summary>
    Latest,
}
