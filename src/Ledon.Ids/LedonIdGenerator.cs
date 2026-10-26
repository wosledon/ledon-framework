namespace Ledon.Ids;

/// <summary>默认 ID 生成器：ULID + GUID v7 + Snowflake。</summary>
public sealed class LedonIdGenerator(SnowflakeIdGenerator snowflake) : IIdGenerator
{
    public string NewId() => NewUlid().ToString();

    public Guid NewGuid() => Guid.CreateVersion7();

    public Ulid NewUlid() => Ulid.New();

    public long NewSnowflake() => snowflake.NextId();
}
