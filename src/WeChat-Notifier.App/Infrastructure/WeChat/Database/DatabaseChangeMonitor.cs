namespace WeChat_Notifier.Infrastructure.WeChat.Database;

internal readonly record struct DatabaseWake(bool Periodic, Exception? Error);

/// <summary>文件事件只唤醒后台读取；重复事件合并，连续写入最多等待一秒。</summary>
internal sealed class DatabaseChangeMonitor : IDisposable
{
    internal const int DebounceMs = 125, MaximumDelayMs = 1000;
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = [];
    private long? _firstChange;
    private long _lastChange, _lastCheck = Environment.TickCount64;
    private bool _immediate, _disposed;
    private Exception? _error;

    internal DatabaseChangeMonitor(IEnumerable<string> databases)
    {
        foreach (var path in databases)
        {
            var full = Path.GetFullPath(path);
            _paths.Add(full);
            _paths.Add(full + "-wal");
            _paths.Add(full + "-shm");
        }
        try
        {
            foreach (var directory in _paths.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var watcher = new FileSystemWatcher(directory!)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    IncludeSubdirectories = false
                };
                watcher.Changed += OnChange;
                watcher.Created += OnChange;
                watcher.Deleted += OnChange;
                watcher.Renamed += (_, args) =>
                {
                    if (Matches(args.FullPath) || Matches(args.OldFullPath)) RequestCheck();
                };
                watcher.Error += (_, args) => SignalError(args.GetException());
                _watchers.Add(watcher);
                watcher.EnableRaisingEvents = true;
            }
        }
        catch { Dispose(); throw; }
    }

    internal bool Matches(string path) => _paths.Contains(Path.GetFullPath(path));
    private void OnChange(object sender, FileSystemEventArgs args)
    {
        if (Matches(args.FullPath)) RequestCheck();
    }

    internal void RequestCheck(bool immediate = false)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var now = Environment.TickCount64;
            _firstChange ??= now;
            _lastChange = now;
            _immediate |= immediate;
            _wake.Set();
        }
    }

    internal void SignalError(Exception error)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _error = _error == null ? error : new AggregateException(_error, error);
            RequestCheck(immediate: true);
        }
    }

    internal static long DueAt(long first, long last) => Math.Min(last + DebounceMs, first + MaximumDelayMs);

    internal DatabaseWake? WaitForCheck(CancellationToken stop, int checkIntervalMs)
    {
        var handles = new[] { stop.WaitHandle, _wake };
        while (!stop.IsCancellationRequested)
        {
            int wait;
            lock (_gate)
            {
                var now = Environment.TickCount64;
                var periodic = now >= _lastCheck + checkIntervalMs;
                var due = _firstChange.HasValue ? DueAt(_firstChange.Value, _lastChange) : long.MaxValue;
                if (_immediate || periodic || now >= due)
                {
                    var result = new DatabaseWake(periodic, _error);
                    _firstChange = null;
                    _immediate = false;
                    _error = null;
                    if (periodic) _lastCheck = now;
                    return result;
                }
                wait = checked((int)Math.Min(_lastCheck + checkIntervalMs - now, due - now));
            }
            if (WaitHandle.WaitAny(handles, wait) == 0) return null;
        }
        return null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        _wake.Dispose();
    }
}
