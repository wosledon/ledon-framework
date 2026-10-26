using System.Collections.Concurrent;
using System.Text.Json;

namespace Ledon.Settings;

/// <summary>进程内设置存储（测试/单实例）。</summary>
public sealed class InMemorySettingsStore : ISettingsStore
{
    private readonly ConcurrentDictionary<string, SettingEntry> _entries = new(StringComparer.Ordinal);

    public Task<SettingEntry?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_entries.TryGetValue(key, out var entry) ? entry : null);

    public Task<IReadOnlyList<SettingEntry>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SettingEntry>>(_entries.Values.OrderBy(entry => entry.Key).ToArray());

    public Task SetAsync(SettingEntry entry, CancellationToken cancellationToken = default)
    {
        _entries[entry.Key] = entry;
        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_entries.TryRemove(key, out _));
}

/// <summary>
/// JSON 文件设置存储：启动时加载、写入后原子替换文件。
/// 适合单实例/开发；多实例请替换为共享数据库实现。
/// </summary>
public sealed class JsonFileSettingsStore : ISettingsStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SettingEntry> _entries = new(StringComparer.Ordinal);
    private readonly Timer _reloadTimer;

    public JsonFileSettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = System.IO.Path.GetFullPath(path);
        Reload();

        // 其他实例/进程写入后自动刷新（单实例内也保持一致）
        _reloadTimer = new Timer(_ => Reload(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public Task<SettingEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return Task.FromResult(_entries.TryGetValue(key, out var entry) ? entry : null);
    }

    public Task<IReadOnlyList<SettingEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<SettingEntry>>(_entries.Values.OrderBy(entry => entry.Key).ToArray());
    }

    public Task SetAsync(SettingEntry entry, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _entries[entry.Key] = entry;
            Persist();
        }

        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_entries.Remove(key))
                return Task.FromResult(false);

            Persist();
            return Task.FromResult(true);
        }
    }

    public void Dispose() => _reloadTimer.Dispose();

    private void Reload()
    {
        try
        {
            if (!File.Exists(_path))
                return;

            var payload = File.ReadAllText(_path);
            var entries = JsonSerializer.Deserialize<SettingEntry[]>(payload, JsonOptions) ?? [];

            lock (_gate)
            {
                _entries.Clear();
                foreach (var entry in entries)
                    _entries[entry.Key] = entry;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 文件正在写入/损坏：保留上一份可用快照，下次刷新重试
        }
    }

    private void Persist()
    {
        var directory = System.IO.Path.GetDirectoryName(_path);

        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temp = $"{_path}.tmp-{Guid.NewGuid():N}";

        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(_entries.Values, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (File.Exists(temp))
                File.Delete(temp);

            throw;
        }
    }
}
