using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using WeChat_Notifier.Domain;
using WeChat_Notifier.Configuration;
using WeChat_Notifier.Infrastructure.Logging;
using WeChat_Notifier.Infrastructure.Windows.Interop;

namespace WeChat_Notifier.Infrastructure.Windows;

internal sealed class TrayIcon : IDisposable
{
    private readonly NativeWindow _window = new("WeChatNotifier.Tray");
    private readonly AppConfig _config;
    private nint _icon;
    private readonly nint _blankIcon;
    private readonly DispatcherQueueTimer _flashTimer;
    private long _flashUntilTick;
    private bool _iconVisible = true;
    private NotificationMode _mode = NotificationMode.Normal;
    private int _unreadCount;
    private bool _disposed;
    private readonly uint _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
    private bool _added;
    private string _state = "等待微信";
    public event Action? SettingsRequested, TogglePauseRequested, TestRequested, ReloadRequested, ExitRequested;
    public event Action? WeChatRequested;
    public event Action? SystemResumed;
    public event Action<NotificationMode>? ModeRequested;
    public bool Paused { get; private set; }
    internal bool IsFlashing => _flashTimer.IsRunning;
    internal int UnreadCount => _unreadCount;
    internal NotificationMode Mode => _mode;
    internal string ModeMenuLabel => $"模式（当前：{_mode.DisplayName()}）";

    public TrayIcon(DispatcherQueue dispatcher, AppConfig config)
    {
        _config = config;
        _icon = IconLoader.CreateTrayIcon(_mode, _unreadCount);
        _blankIcon = IconLoader.CreateBlankIcon();
        _flashTimer = dispatcher.CreateTimer();
        _flashTimer.Interval = TimeSpan.FromMilliseconds(500);
        _flashTimer.Tick += (_, _) => FlashTick();
        _window.MessageHandler = OnMessage;
        var args = Environment.GetCommandLineArgs();
        if (!args.Contains("--ui-smoke") && !args.Contains("--shot-settings")) Add();
    }

    private Native.NOTIFYICONDATA Data() => new()
    {
        cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(), hWnd = _window.Handle,
        uID = 1, uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP,
        uCallbackMessage = Native.WM_TRAYICON, hIcon = _iconVisible ? _icon : _blankIcon,
        szTip = $"微信通知 · {_mode.DisplayName()} · 新消息 {_unreadCount} — " + (Paused ? "已暂停" : _state),
        szInfo = "", szInfoTitle = ""
    };

    private void Add()
    {
        var data = Data();
        if (!Native.Shell_NotifyIcon(Native.NIM_ADD, ref data))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法添加托盘图标。");
        _added = true;
        data.uVersion = Native.NOTIFYICON_VERSION_4;
        if (!Native.Shell_NotifyIcon(Native.NIM_SETVERSION, ref data))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法设置托盘图标版本。");
    }

    public void SetState(string state)
    {
        _state = state switch { "connected" => "已连接微信", "notification-error" => "通知失败，请查看日志", _ => "等待微信" };
        Update();
    }

    public void SetPaused(bool paused) { Paused = paused; Update(); }
    public void SetMode(NotificationMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        ReplaceIcon();
    }

    public void SetUnreadCount(int count)
    {
        count = Math.Max(0, count);
        if (_unreadCount == count) return;
        _unreadCount = count;
        if (count == 0) StopFlashing();
        else if (_config.TrayFlashUntilRead) StartFlashing();
        ReplaceIcon();
    }

    public void Flash() => StartFlashing();

    internal void ApplyFlashSettings()
    {
        if (_unreadCount == 0)
        {
            StopFlashing();
            return;
        }

        if (_config.TrayFlashUntilRead)
        {
            StartFlashing();
            return;
        }

        if (_flashTimer.IsRunning && _flashUntilTick == long.MaxValue) StartFlashing();
    }

    private void StartFlashing()
    {
        if (_unreadCount == 0) return;
        _flashUntilTick = _config.TrayFlashUntilRead
            ? long.MaxValue
            : Environment.TickCount64 + _config.TrayFlashDurationSeconds * 1000L;
        if (_flashTimer.IsRunning) return;
        _iconVisible = true;
        _flashTimer.Start();
    }

    private void StopFlashing()
    {
        _flashTimer.Stop();
        _flashUntilTick = 0;
        _iconVisible = true;
    }

    private void FlashTick()
    {
        if (_unreadCount == 0 || (!_config.TrayFlashUntilRead && Environment.TickCount64 >= _flashUntilTick))
        {
            StopFlashing();
        }
        else _iconVisible = !_iconVisible;
        Update();
    }

    private void ReplaceIcon()
    {
        var next = IconLoader.CreateTrayIcon(_mode, _unreadCount);
        var old = _icon;
        _icon = next;
        Update();
        Native.DestroyIcon(old);
    }
    private void Update()
    {
        if (!_added || _disposed) return;
        var data = Data();
        if (!Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref data))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "更新托盘状态失败。");
    }

    private nint? OnMessage(uint message, nint wParam, nint lParam)
    {
        if (message == 0x0218 && wParam is 0x0007 or 0x0012)
        {
            SystemResumed?.Invoke();
            return 1;
        }
        if (message == _taskbarCreated && _taskbarCreated != 0) { _added = false; Add(); return 0; }
        if (message != Native.WM_TRAYICON) return null;
        var gesture = (int)((long)lParam & 0xffff);
        if (gesture == Native.WM_LBUTTONDBLCLK) WeChatRequested?.Invoke();
        else if (gesture is Native.WM_RBUTTONUP or 0x007B) ShowMenu();
        return 0;
    }

    private void ShowMenu()
    {
        var menu = CreateMenu();
        try
        {
            if (!Native.GetCursorPos(out var point)) throw new Win32Exception(Marshal.GetLastWin32Error());
            Native.SetForegroundWindow(_window.Handle);
            var command = Native.TrackPopupMenuEx(menu, Native.TPM_RETURNCMD | Native.TPM_NONOTIFY | Native.TPM_RIGHTBUTTON,
                point.X, point.Y, _window.Handle, 0);
            Native.PostMessage(_window.Handle, Native.WM_NULL, 0, 0);
            ExecuteCommand(command);
        }
        finally { Native.DestroyMenu(menu); }
    }

    // 调用方拥有根菜单；DestroyMenu 同时释放已挂接的二级菜单。
    internal nint CreateMenu()
    {
        var menu = Native.CreatePopupMenu();
        if (menu == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        nint modes = 0;
        try
        {
            void Item(int id, string label) { if (!Native.AppendMenu(menu, Native.MF_STRING, id, label)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
            Item(1, "设置…");
            modes = Native.CreatePopupMenu();
            if (modes == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            foreach (var mode in Enum.GetValues<NotificationMode>())
                if (!Native.AppendMenu(modes, Native.MF_STRING | (mode == _mode ? Native.MF_CHECKED : 0),
                    10 + (int)mode, mode.DisplayName())) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!Native.AppendMenu(menu, Native.MF_POPUP, modes, ModeMenuLabel))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            modes = 0;
            Item(2, Paused ? "恢复通知" : "暂停通知"); Item(3, "发送测试通知");
            Native.AppendMenu(menu, Native.MF_SEPARATOR, 0, null!);
            Item(4, "重新加载配置"); Item(5, "打开配置文件"); Item(6, "打开日志目录");
            Native.AppendMenu(menu, Native.MF_SEPARATOR, 0, null!);
            Item(7, "退出");
            return menu;
        }
        catch
        {
            if (modes != 0) Native.DestroyMenu(modes);
            Native.DestroyMenu(menu);
            throw;
        }
    }

    internal void ExecuteCommand(int command)
    {
        switch (command)
        {
            case 1: SettingsRequested?.Invoke(); break;
            case 2: TogglePauseRequested?.Invoke(); break;
            case 3: TestRequested?.Invoke(); break;
            case 4: ReloadRequested?.Invoke(); break;
            case 5: ShellOpen.File(AppPaths.ConfigFile); break;
            case 6: ShellOpen.Folder(AppPaths.LogDirectory); break;
            case 7: ExitRequested?.Invoke(); break;
            case >= 10 and <= 12: ModeRequested?.Invoke((NotificationMode)(command - 10)); break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _flashTimer.Stop();
        if (_added)
        {
            var data = Data();
            if (!Native.Shell_NotifyIcon(Native.NIM_DELETE, ref data))
                Log.Warn("移除托盘图标失败：" + Marshal.GetLastWin32Error());
            _added = false;
        }
        _window.Dispose();
        Native.DestroyIcon(_icon);
        Native.DestroyIcon(_blankIcon);
    }
}
