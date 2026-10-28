using Microsoft.UI.Dispatching;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WeChat_Notifier.Domain;
using WeChat_Notifier.Infrastructure.Logging;
using WeChat_Notifier.Infrastructure.Windows.Interop;

namespace WeChat_Notifier.Infrastructure.WeChat;

/// <summary>窗口事件即时更新状态；低频进程发现与热键共用同一状态，回调不读取数据库。</summary>
internal sealed unsafe class WeChatStateMonitor : IDisposable
{
    private const uint ForegroundEvent = 3, MinimizeStart = 0x16, MinimizeEnd = 0x17;
    private const uint WindowDestroy = 0x8001, WindowShow = 0x8002, WindowHide = 0x8003;
    private static readonly Dictionary<nint, WeChatStateMonitor> Owners = [];
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _discovery;
    private readonly HashSet<uint> _processIds = [];
    private readonly List<nint> _hooks = [];
    private int _state;
    private bool _refreshQueued, _disposed;
    public event Action? Changed;
    public WeChatWindowState Current => (WeChatWindowState)Volatile.Read(ref _state);
    public bool IsRunning => Current != WeChatWindowState.NotRunning;
    public string StatusText => Current switch
    {
        WeChatWindowState.Foreground => "微信在前台",
        WeChatWindowState.Background => "微信在后台",
        WeChatWindowState.Minimized => "微信已最小化",
        WeChatWindowState.Hidden => "微信已隐藏到托盘",
        WeChatWindowState.NotFound => "等待微信窗口",
        _ => "等待微信启动"
    };

    public WeChatStateMonitor(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _discovery = dispatcher.CreateTimer();
        _discovery.Interval = TimeSpan.FromSeconds(1);
        _discovery.Tick += (_, _) => Refresh();
    }

    public void Start()
    {
        try
        {
            foreach (var (first, last) in new[]
                     { (ForegroundEvent, ForegroundEvent), (MinimizeStart, MinimizeEnd), (WindowDestroy, WindowHide) })
            {
                var hook = Native.SetWinEventHook(first, last, 0,
                    (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, int, int, uint, uint, void>)&OnEvent, 0, 0, 0);
                if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法监听微信窗口状态。");
                Owners.Add(hook, this);
                _hooks.Add(hook);
            }
            Refresh();
            _discovery.Start();
        }
        catch { Dispose(); throw; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnEvent(nint hook, uint ev, nint hwnd, int objectId, int childId, uint threadId, uint time)
    {
        try
        {
            if (!Owners.TryGetValue(hook, out var owner) || owner._disposed) return;
            if (ev is not (ForegroundEvent or MinimizeStart or MinimizeEnd) && (objectId != 0 || childId != 0)) return;
            if (ev != ForegroundEvent)
            {
                Native.GetWindowThreadProcessId(hwnd, out var pid);
                if (!owner._processIds.Contains(pid)) return;
            }
            if (ev is not (ForegroundEvent or MinimizeStart or MinimizeEnd or WindowDestroy or WindowShow or WindowHide)) return;
            owner.QueueRefresh();
        }
        catch (Exception ex) { Log.Error("微信窗口状态事件失败", ex); }
    }

    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        if (_dispatcher.TryEnqueue(() => { _refreshQueued = false; if (!_disposed) RefreshWindowState(); })) return;
        _refreshQueued = false;
        Log.Warn("无法投递微信窗口状态更新，UI 队列已关闭。");
    }

    private void Refresh()
    {
        _processIds.Clear();
        foreach (var name in WeChatWindow.ProcessNames)
        {
            var processes = System.Diagnostics.Process.GetProcessesByName(name);
            try { foreach (var process in processes) _processIds.Add((uint)process.Id); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        RefreshWindowState();
    }

    private void RefreshWindowState()
    {
        var hwnd = WeChatWindow.Find(_processIds);
        var foreground = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(foreground, out var foregroundPid);
        var state = Resolve(_processIds.Count != 0, hwnd != 0,
            hwnd != 0 && Native.IsWindowVisible(hwnd), hwnd != 0 && Native.IsIconic(hwnd),
            foreground != 0 && _processIds.Contains(foregroundPid) && Native.IsWindowVisible(foreground) && !Native.IsIconic(foreground));
        if (Current == state) return;
        Volatile.Write(ref _state, (int)state);
        Changed?.Invoke();
    }

    internal static WeChatWindowState Resolve(bool running, bool found, bool visible, bool minimized, bool foreground)
    {
        if (!running) return WeChatWindowState.NotRunning;
        if (foreground) return WeChatWindowState.Foreground;
        if (!found) return WeChatWindowState.NotFound;
        if (!visible) return WeChatWindowState.Hidden;
        return minimized ? WeChatWindowState.Minimized : WeChatWindowState.Background;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _discovery.Stop();
        foreach (var hook in _hooks)
        {
            if (!Native.UnhookWinEvent(hook)) Log.Warn("释放微信窗口状态监听失败：" + Marshal.GetLastWin32Error());
            Owners.Remove(hook);
        }
        _hooks.Clear();
    }
}
