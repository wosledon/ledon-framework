namespace Ledon.Settings;

/// <summary>一条运行时设置。</summary>
public sealed record SettingEntry
{
    /// <summary>设置键（建议 <c>模块:键</c> 形式）。</summary>
    public required string Key { get; init; }

    /// <summary>序列化后的值（加密时为密文）。</summary>
    public required string Value { get; init; }

    /// <summary>是否加密存储。</summary>
    public bool Encrypted { get; init; }

    /// <summary>最后修改时间（UTC）。</summary>
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>最后修改人（可选）。</summary>
    public string? UpdatedBy { get; init; }
}

/// <summary>设置存储（默认内存/JSON 文件；可替换为数据库/配置中心实现）。</summary>
public interface ISettingsStore
{
    Task<SettingEntry?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SettingEntry>> ListAsync(CancellationToken cancellationToken = default);

    Task SetAsync(SettingEntry entry, CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>运行时设置服务：强类型读写 + 可选加密。</summary>
public interface ISettingsService
{
    /// <summary>读取强类型设置；不存在返回 default。</summary>
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);

    /// <summary>读取强类型设置；不存在抛 <see cref="KeyNotFoundException"/>。</summary>
    Task<T> GetRequiredAsync<T>(string key, CancellationToken cancellationToken = default);

    /// <summary>写入强类型设置。</summary>
    Task SetAsync<T>(
        string key,
        T value,
        bool encrypted = false,
        string? updatedBy = null,
        CancellationToken cancellationToken = default);

    /// <summary>删除设置。</summary>
    Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>读取某一前缀下的全部设置（如 <c>smtp:</c>）。</summary>
    Task<IReadOnlyDictionary<string, T>> GetAllAsync<T>(
        string? prefix = null,
        CancellationToken cancellationToken = default);
}

/// <summary>设置选项。</summary>
public sealed class LedonSettingsOptions
{
    /// <summary>JSON 文件路径；为空时使用进程内存储。</summary>
    public string? StorePath { get; set; }

    /// <summary>Data Protection 用途名，默认 <c>Ledon.Settings</c>。</summary>
    public string EncryptionPurpose { get; set; } = "Ledon.Settings";
}
