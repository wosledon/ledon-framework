using Microsoft.Extensions.Logging;

namespace Ledon.Notifications;

/// <summary>内存发送记录（单实例/开发）。</summary>
public sealed class InMemoryNotificationLog : INotificationLog
{
    private readonly Lock _gate = new();
    private readonly List<NotificationRecord> _records = [];
    private readonly int _capacity;

    public InMemoryNotificationLog(int capacity = 500) => _capacity = capacity;

    public void Record(Notification notification, NotificationResult result, int attempts)
    {
        var record = new NotificationRecord(
            Guid.NewGuid(),
            notification.Channel,
            notification.Recipient,
            notification.Subject,
            notification.TemplateName,
            result.Accepted,
            result.Error,
            DateTimeOffset.UtcNow,
            attempts);

        lock (_gate)
        {
            _records.Add(record);

            if (_records.Count > _capacity)
                _records.RemoveRange(0, _records.Count - _capacity);
        }
    }

    public IReadOnlyList<NotificationRecord> Recent(int count = 50)
    {
        lock (_gate)
        {
            return _records
                .OrderByDescending(record => record.CreatedAtUtc)
                .Take(Math.Max(1, count))
                .ToArray();
        }
    }

    public IReadOnlyDictionary<string, (int Total, int Failed)> Stats()
    {
        lock (_gate)
        {
            return _records
                .GroupBy(record => record.Channel)
                .ToDictionary(
                    group => group.Key,
                    group => (Total: group.Count(), Failed: group.Count(record => !record.Success)));
        }
    }
}
