namespace Ledon.Ids;

/// <summary>ID 生成器：默认 ULID；同时提供 GUID v7 与 Snowflake。</summary>
public interface IIdGenerator
{
    /// <summary>默认 ID（ULID 字符串，可按时间排序）。</summary>
    string NewId();

    /// <summary>GUID v7（时间有序）。</summary>
    Guid NewGuid();

    /// <summary>ULID 强度值。</summary>
    Ulid NewUlid();

    /// <summary>Snowflake 64 位 ID（趋势递增）。</summary>
    long NewSnowflake();
}

/// <summary>Snowflake 参数。</summary>
public sealed class LedonIdOptions
{
    /// <summary>工作节点 ID（0-1023），多实例部署必须唯一。</summary>
    public long WorkerId { get; set; }

    /// <summary>起始纪元（UTC），默认 2024-01-01。</summary>
    public DateTimeOffset Epoch { get; set; } = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
}

/// <summary>Snowflake 生成器：时间戳 + workerId + 12 位序列，单节点趋势递增。</summary>
public sealed class SnowflakeIdGenerator : IDisposable
{
    private const int WorkerIdBits = 10;
    private const int SequenceBits = 12;
    private const long MaxWorkerId = (1L << WorkerIdBits) - 1;
    private const long MaxSequence = (1L << SequenceBits) - 1;

    private readonly Lock _gate = new();
    private readonly long _epochMilliseconds;
    private readonly long _workerId;
    private long _lastTimestamp = -1;
    private long _sequence;

    public SnowflakeIdGenerator(long workerId, DateTimeOffset? epoch = null)
    {
        if (workerId is < 0 or > MaxWorkerId)
            throw new ArgumentOutOfRangeException(nameof(workerId), workerId, $"workerId 必须在 0-{MaxWorkerId} 之间");

        _workerId = workerId;
        _epochMilliseconds = (epoch ?? new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)).ToUnixTimeMilliseconds();
    }

    /// <summary>生成下一个 ID。</summary>
    public long NextId()
    {
        lock (_gate)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _epochMilliseconds;

            if (timestamp < 0)
                throw new InvalidOperationException("系统时间早于 Snowflake 纪元");

            if (timestamp <= _lastTimestamp)
            {
                // 同一毫秒或时钟回拨：沿用上次时间戳并递增序列，保证不重复
                timestamp = _lastTimestamp;
                _sequence = (_sequence + 1) & MaxSequence;

                if (_sequence == 0)
                {
                    // 当前毫秒序列用尽：等到下一毫秒
                    while (timestamp <= _lastTimestamp)
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _epochMilliseconds;
                }
            }
            else
            {
                _sequence = 0;
            }

            _lastTimestamp = timestamp;

            return (timestamp << (WorkerIdBits + SequenceBits))
                | (_workerId << SequenceBits)
                | _sequence;
        }
    }

    public void Dispose()
    {
        // 无托管/非托管资源；保留 IDisposable 以便未来扩展
    }
}
