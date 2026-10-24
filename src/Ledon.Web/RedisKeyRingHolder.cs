using StackExchange.Redis;

namespace Ledon.Web;

/// <summary>
/// Data Protection 码环的 Redis 连接持有者：惰性建连（首次读写密钥时才连，宿主持续由容器释放）。
/// AbortOnConnectFail=false 由调用方在 <see cref="ConfigurationOptions"/> 上设置。
/// </summary>
internal sealed class RedisKeyRingHolder(ConfigurationOptions options) : IDisposable
{
    private readonly Lazy<IConnectionMultiplexer> _multiplexer = new(
        () => ConnectionMultiplexer.Connect(options));

    public IDatabase Database => _multiplexer.Value.GetDatabase();

    public void Dispose()
    {
        if (_multiplexer.IsValueCreated)
            _multiplexer.Value.Dispose();
    }
}
