using WeChat_Notifier.Domain;

namespace WeChat_Notifier.Application.Notifications;

/// <summary>只计运行期间已投递的新消息；启动时的历史未读不变成新角标。</summary>
internal sealed class UnreadNotifications
{
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _lastUnread = new(StringComparer.Ordinal);
    internal int Count => _counts.Values.Sum();

    internal void Arrived(SessionInfo session) =>
        _counts[session.Key] = _counts.GetValueOrDefault(session.Key) + session.NewMessageCount;

    internal void Synchronize(IReadOnlyList<WeChatSession> sessions)
    {
        var current = sessions.ToDictionary(s => s.Username, s => Math.Max(0, s.Unread), StringComparer.Ordinal);
        foreach (var key in _counts.Keys.ToArray())
        {
            if (!current.TryGetValue(key, out var unread) || unread == 0) { _counts.Remove(key); continue; }
            var decrease = Math.Max(0, _lastUnread.GetValueOrDefault(key) - unread);
            _counts[key] = Math.Min(unread, Math.Max(0, _counts[key] - decrease));
            if (_counts[key] == 0) _counts.Remove(key);
        }
        _lastUnread.Clear();
        foreach (var (key, value) in current) _lastUnread[key] = value;
    }

    internal void Acknowledge() => _counts.Clear();
}
