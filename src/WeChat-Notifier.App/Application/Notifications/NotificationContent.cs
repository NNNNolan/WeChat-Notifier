using WeChat_Notifier.Domain;

namespace WeChat_Notifier.Application.Notifications;

internal static class NotificationContent
{
    internal static SessionInfo IncludeExistingCount(SessionInfo latest, int existingCount)
    {
        var incoming = Math.Max(1, latest.NewMessageCount);
        var count = existingCount > 0
            ? (int)Math.Min(int.MaxValue, (long)existingCount + incoming)
            : incoming;

        return new SessionInfo
        {
            Key = latest.Key,
            Title = latest.Title,
            Preview = count > 1 ? $"[{count}条] {latest.Preview}" : latest.Preview,
            Time = latest.Time,
            Muted = latest.Muted,
            Unread = latest.Unread,
            NewMessageCount = count
        };
    }
}
