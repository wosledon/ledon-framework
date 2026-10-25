using System.Collections.Concurrent;
using System.Text.Json;

namespace Ledon.AuditLog;

/// <summary>内存审计存储（默认）：单实例/开发用，进程重启即失。</summary>
public sealed class InMemoryAuditStore : IAuditStore
{
    private readonly Lock _gate = new();
    private readonly List<AuditEntry> _entries = [];
    private readonly int _capacity;

    public InMemoryAuditStore(int capacity = 2000) => _capacity = capacity;

    public Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _entries.Add(entry);

            if (_entries.Count > _capacity)
                _entries.RemoveRange(0, _entries.Count - _capacity);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEntry>> QueryAsync(
        string? entityType = null, string? entityId = null, int count = 50, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<AuditEntry>>(_entries
                .Where(entry => entityType is null || entry.EntityType.Equals(entityType, StringComparison.OrdinalIgnoreCase))
                .Where(entry => entityId is null || entry.EntityId.Equals(entityId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => entry.TimestampUtc)
                .Take(Math.Max(1, count))
                .ToArray());
        }
    }

    public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var removed = _entries.RemoveAll(entry => entry.TimestampUtc < cutoffUtc);
            return Task.FromResult(removed);
        }
    }
}

/// <summary>变更 JSON 序列化（稳定、可读）。</summary>
internal static class AuditJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    public static string Serialize(IEnumerable<PropertyChange> changes) =>
        JsonSerializer.Serialize(changes, Options);
}
