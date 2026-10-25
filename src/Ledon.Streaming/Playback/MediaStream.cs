using System.Collections.Concurrent;
using System.Threading.Channels;
using Ledon.Streaming.Media;
using Microsoft.Extensions.Logging;

namespace Ledon.Streaming.Playback;

/// <summary>
/// 一条直播流的媒体管线：发布者写入 FLV Tag，订阅者按各自节奏读取。
/// <para>
/// 关键设计：
/// </para>
/// <list type="bullet">
///   <item>**GOP 缓存**：新订阅者先收到最近一个 GOP（关键帧起的连续帧），
///         否则播放器要等下一个关键帧才能出画面（最长一个 GOP 时长，通常 1-2 秒）；</item>
///   <item>**背压**：每个订阅者有界队列，慢了就丢帧而不是拖垮发布者（直播场景合理）；</item>
///   <item>慢订阅者断开只影响自己，不影响发布者和其他观众。</item>
/// </list>
/// </summary>
public sealed class MediaStream
{
    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();
    private readonly List<FlvTag> _gopCache = [];
    private readonly Lock _gate = new();
    private readonly ILogger _logger;

    private FlvTag? _sequenceHeader;
    private uint _lastTimestamp;
    private long _tagCount;
    private long _droppedForSlow;

    internal MediaStream(StreamIdentity identity, ILogger logger)
    {
        Identity = identity;
        _logger = logger;
    }

    /// <summary>流标识。</summary>
    public StreamIdentity Identity { get; }

    /// <summary>是否有人发布。</summary>
    public bool IsPublished { get; private set; }

    /// <summary>当前观众数。</summary>
    public int SubscriberCount => _subscribers.Count;

    /// <summary>累计写入 Tag 数。</summary>
    public long TagCount => Interlocked.Read(ref _tagCount);

    /// <summary>最近一个 Tag 的时间戳（毫秒）。</summary>
    public uint LastTimestampMs => Interlocked.CompareExchange(ref _lastTimestamp, 0, 0);

    /// <summary>发布开始时间（UTC）。</summary>
    public DateTimeOffset? PublishedAtUtc { get; private set; }

    /// <summary>发布者信息。</summary>
    public string? PublisherId { get; private set; }

    /// <summary>停止发布时间（UTC）；未停止为 null。过期清扫靠它判断。</summary>
    public DateTimeOffset? UnpublishedAtUtc { get; private set; }

    /// <summary>累计写入的媒体字节数（负载，不含封装头）。</summary>
    public long TotalBytes => Interlocked.Read(ref _totalBytes);

    private long _totalBytes;

    /// <summary>标记为已发布。</summary>
    public void Publish(string? publisherId)
    {
        lock (_gate)
        {
            IsPublished = true;
            PublisherId = publisherId;
            PublishedAtUtc = DateTimeOffset.UtcNow;
            UnpublishedAtUtc = null;
            _gopCache.Clear();
            _sequenceHeader = null;
        }

        _logger.LogInformation("流 {Stream} 开始发布（{Publisher}）", Identity, publisherId);
    }

    /// <summary>结束发布：观众会陆续收不到新数据并断开。</summary>
    public void Unpublish()
    {
        lock (_gate)
        {
            IsPublished = false;
            PublisherId = null;
            UnpublishedAtUtc = DateTimeOffset.UtcNow;
        }

        _logger.LogInformation("流 {Stream} 停止发布", Identity);
    }

    /// <summary>写入一个媒体 Tag（发布者路径）。</summary>
    public void Write(FlvTag tag)
    {
        Interlocked.Increment(ref _tagCount);
        Interlocked.Exchange(ref _lastTimestamp, tag.TimestampMs);
        Interlocked.Add(ref _totalBytes, tag.Data.Length);

        lock (_gate)
        {
            // 序列头（AVC/AAC 的 sequence header）必须随 GOP 缓存，否则新观众解不开
            if (IsSequenceHeader(tag))
            {
                _sequenceHeader = tag;
                _gopCache.Clear();
            }

            if (tag.IsKeyFrame)
                _gopCache.Clear();   // 新 GOP 开始

            _gopCache.Add(tag);
            if (_gopCache.Count > MaxGopFrames)
                _gopCache.RemoveAt(0);
        }

        foreach (var subscriber in _subscribers.Values)
            subscriber.Post(tag);
    }

    /// <summary>订阅：返回读取器。先补发 GOP 缓存，让播放器立即出画面。</summary>
    public StreamSubscription Subscribe()
    {
        var subscriber = new Subscriber();
        _subscribers[subscriber.Id] = subscriber;

        // 补发 GOP：缓存里已含序列头（写入序列头时会清场重开 GOP），
        // 新观众不必等下一个关键帧就能出画面
        lock (_gate)
        {
            foreach (var tag in _gopCache)
                subscriber.Post(tag);
        }

        _logger.LogDebug("流 {Stream} 新增观众（当前 {Count} 人）", Identity, _subscribers.Count);

        return new StreamSubscription(subscriber.Id, subscriber.Reader);
    }

    /// <summary>取消订阅。</summary>
    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var subscriber))
        {
            subscriber.Complete();
            _logger.LogDebug("流 {Stream} 观众离开（当前 {Count} 人）", Identity, _subscribers.Count);
        }
    }

    /// <summary>快照（运维/统计）。</summary>
    public MediaStreamStats Stats() => new(
        Identity.Path,
        IsPublished,
        _subscribers.Count,
        TagCount,
        LastTimestampMs,
        PublishedAtUtc,
        Interlocked.Read(ref _droppedForSlow),
        TotalBytes);

    internal void ReportDrop() => Interlocked.Increment(ref _droppedForSlow);

    /// <summary>GOP 缓存上限（约 3 秒 @30fps，兼顾内存与起播速度）。</summary>
    private const int MaxGopFrames = 90;

    /// <summary>
    /// 判断是否为序列头（AVC sequence header：AVCPacketType = 0）。
    /// 规范上序列头只随关键帧出现，因此同时要求是关键帧——
    /// 否则帧间帧（AVCPacketType 也是 0 的异常流）会被误判，导致 GOP 缓存被反复清空。
    /// </summary>
    private static bool IsSequenceHeader(FlvTag tag)
    {
        if (!tag.IsKeyFrame || tag.Data.Length < 5)
            return false;

        var codec = (FlvVideoCodec)(tag.Data[0] & 0x0F);
        if (codec != FlvVideoCodec.Avc)
            return false;

        var avcPacketType = tag.Data[1];
        return avcPacketType == 0;   // 0 = AVC sequence header
    }

    private sealed class Subscriber
    {
        private readonly Channel<FlvTag> _channel;

        public Subscriber()
        {
            _channel = Channel.CreateBounded<FlvTag>(new BoundedChannelOptions(120)
            {
                FullMode = BoundedChannelFullMode.DropOldest,   // 背压：丢最旧的，保住最新画面
                SingleReader = true,
                SingleWriter = false,
            });
        }

        public Guid Id { get; } = Guid.NewGuid();

        public ChannelReader<FlvTag> Reader => _channel.Reader;

        public void Post(FlvTag tag)
        {
            if (!_channel.Writer.TryWrite(tag))
            {
                // DropOldest 模式下 TryWrite 总是成功；理论上不会到这里
            }
        }

        public void Complete() => _channel.Writer.TryComplete();
    }
}

/// <summary>
/// 一次订阅的句柄：用 <see cref="Id"/> 取消订阅，用 <see cref="Reader"/> 读媒体。
/// </summary>
/// <param name="Id">订阅标识（传给 <see cref="MediaStream.Unsubscribe"/>）。</param>
/// <param name="Reader">媒体 Tag 读取器（读完即断流，须取消订阅释放队列）。</param>
public readonly record struct StreamSubscription(Guid Id, ChannelReader<FlvTag> Reader);

/// <summary>流的统计快照。</summary>
public sealed record MediaStreamStats(
    string Stream,
    bool IsPublished,
    int Subscribers,
    long Tags,
    uint LastTimestampMs,
    DateTimeOffset? PublishedAtUtc,
    long DroppedForSlow,
    long TotalBytes = 0);
