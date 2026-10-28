using WeChat_Notifier.Configuration;
using WeChat_Notifier.Domain;
using WeChat_Notifier.Infrastructure.Logging;

namespace WeChat_Notifier.Infrastructure.WeChat.Database;

/// <summary>Reads local session snapshots on a background thread; events run on that thread.</summary>
public sealed class WeChatNotificationSource : IDisposable
{
    private const int ErrorRetryDelayMs = 3000;
    private readonly AppConfig _cfg;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, (int Unread, long Time, string Summary, string Sender)> _previous =
        new(StringComparer.Ordinal);
    private static readonly HashSet<string> IgnoredUsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "brandsessionholder", "brandservicesessionholder", "officialaccounts", "notification_messages",
        "newsapp", "weixin", "weixinreminder", "userexperience_alarm", "floatbottle", "medianote",
        "fmessage", "qqmail", "tmessage", "weibo", "mphelper"
    };
    private static readonly HashSet<string> IgnoredNames = new(StringComparer.Ordinal)
    {
        "公众号", "服务号", "微信团队", "微信支付", "订阅号消息", "折叠的群聊"
    };

    private Thread? _thread;
    private volatile bool _paused;
    private int _resync = 1;
    private bool _connected;
    private int _disposed;

    public event Action<SessionInfo>? MessageArrived;
    public event Action<IReadOnlyList<WeChatSession>>? SessionsUpdated;
    public event Action<string>? ConnectionChanged;
    public event Action<string>? Diagnostic;
    public bool IsPaused => _paused;

    public WeChatNotificationSource(AppConfig cfg) => _cfg = cfg;

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "WeChatNotificationSource" };
        _thread.Start();
    }

    public void SetPaused(bool paused)
    {
        if (_paused == paused) return;
        if (!paused) Nudge();
        _paused = paused;
        Log.Info(paused ? "wechat notifications paused" : "wechat notifications resumed");
    }

    /// <summary>Rebuilds the baseline after config changes or resume, without replay.</summary>
    public void Nudge() => Interlocked.Exchange(ref _resync, 1);

    private void Run()
    {
        WeChatDatabase? db = null;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var delay = Math.Clamp(_cfg.PollIntervalMs, 150, 10000);
                try
                {
                    db ??= WeChatDatabase.Open(msg => Log.Debug("wechat: " + msg));
                    ProcessSessions(db.GetSessions());
                    SetConnected(true);
                }
                catch (Exception ex)
                {
                    Log.Warn($"wechat: {ex.GetType().Name}: {ex.Message} -- retrying in {ErrorRetryDelayMs} ms");
                    Raise(Diagnostic, ex.Message);
                    SetConnected(false);
                    db?.Dispose();
                    db = null;
                    _previous.Clear();
                    Nudge();
                    delay = ErrorRetryDelayMs;
                }
                if (_stop.Token.WaitHandle.WaitOne(delay)) break;
            }
        }
        finally
        {
            db?.Dispose();
            _stop.Dispose();
            Log.Info("wechat notification source stopped");
        }
    }

    // Shared by the live poll loop and the regression check: one notification policy.
    internal void ProcessSessions(List<WeChatSession> current)
    {
        Raise(SessionsUpdated, current.ToArray());
        if (Interlocked.Exchange(ref _resync, 0) != 0)
        {
            _previous.Clear();
            foreach (var s in current) _previous[s.Username] = Fingerprint(s);
            Raise(Diagnostic, "");
            Log.Info($"wechat ready: sessions={current.Count} (baseline, no replay)");
            return;
        }

        foreach (var s in current)
        {
            var fp = Fingerprint(s);
            var had = _previous.TryGetValue(s.Username, out var old);
            _previous[s.Username] = fp;
            if (had && (fp == old || s.Unread < old.Unread)) continue;
            if (!had && s.Unread <= 0) continue;

            var reason = SkipReason(s, had && s.Unread <= old.Unread);
            Log.Debug($"[decide] {s.DisplayName}: unread {(had ? old.Unread.ToString() : "-")}->{s.Unread} => " +
                      (reason == null ? "NOTIFY" : "skip: " + reason));
            if (reason != null) continue;

            var body = s.Summary.Trim();
            if (body.Length == 0) body = "收到一条新消息";
            if (s.IsGroup && !string.IsNullOrWhiteSpace(s.LastSender)
                && !body.StartsWith(s.LastSender + ":", StringComparison.Ordinal))
                body = $"{s.LastSender}: {body}";

            Raise(MessageArrived, new SessionInfo
            {
                Key = s.Username,
                Title = s.DisplayName,
                Preview = body,
                Time = s.LastTimestamp > 1_000_000_000
                    ? DateTimeOffset.FromUnixTimeSeconds(s.LastTimestamp).ToLocalTime().ToString("HH:mm")
                    : DateTime.Now.ToString("HH:mm"),
                Muted = s.Muted,
                Unread = s.Unread
            });
        }
    }

    private string? SkipReason(WeChatSession s, bool noUnreadIncrease)
    {
        if (_paused) return "paused";
        if (noUnreadIncrease && _cfg.SkipOwnMessages)
            return "no unread increase (your own message, or the open chat)";
        if (IgnoredUsers.Contains(s.Username)
            || s.Username.StartsWith("@placeholder_", StringComparison.OrdinalIgnoreCase)
            || s.Username.StartsWith("gh_", StringComparison.OrdinalIgnoreCase)
            || IgnoredNames.Contains(s.Nickname)
            || s.Official && !_cfg.IncludeOfficialAccounts)
            return "ignored session / official account";
        if (s.Muted && _cfg.SkipMuted) return "muted session (消息免打扰)";
        if (_cfg.SkipSessions.Any(x => !string.IsNullOrWhiteSpace(x)
                && (x.Equals(s.Username, StringComparison.OrdinalIgnoreCase)
                    || x.Equals(s.Nickname, StringComparison.OrdinalIgnoreCase)
                    || x.Equals(s.DisplayName, StringComparison.OrdinalIgnoreCase)))
            || _cfg.SkipKeywords.Any(x => !string.IsNullOrWhiteSpace(x)
                && s.DisplayName.Contains(x, StringComparison.OrdinalIgnoreCase)))
            return "filtered by config";
        return null;
    }

    private static (int, long, string, string) Fingerprint(WeChatSession s) =>
        (s.Unread, s.LastTimestamp, s.Summary, s.LastSender);

    private void SetConnected(bool connected)
    {
        if (_connected == connected) return;
        _connected = connected;
        Log.Info("wechat connection: " + (connected ? "connected" : "waiting"));
        Raise(ConnectionChanged, connected ? "connected" : "waiting");
    }

    private static void Raise<T>(Action<T>? handler, T value)
    {
        try { handler?.Invoke(value); }
        catch (Exception ex) { Log.Warn("wechat subscriber failed: " + ex.Message); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_thread == null) { _stop.Dispose(); return; }
        // Only the worker disposes its database/token, even if a memory scan outlasts this wait.
        try { _stop.Cancel(); }
        catch (ObjectDisposedException) { return; } // Worker has already finished cleanup.
        _thread.Join(2000);
    }
}
