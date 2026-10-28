using System.ComponentModel;
using System.Runtime.InteropServices;
using WeChat_Notifier.Configuration;
using WeChat_Notifier.Infrastructure.Logging;
using WeChat_Notifier.Infrastructure.Windows.Interop;

namespace WeChat_Notifier.Infrastructure.Windows;

internal sealed class TrayIcon : IDisposable
{
    private readonly NativeWindow _window = new("WeChatNotifier.Tray");
    private readonly nint _icon = IconLoader.LoadTrayIcon();
    private readonly uint _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
    private bool _added;
    private string _state = "等待微信";
    public event Action? SettingsRequested, TogglePauseRequested, TestRequested, ReloadRequested, ExitRequested;
    public bool Paused { get; private set; }

    public TrayIcon()
    {
        _window.MessageHandler = OnMessage;
        Add();
    }

    private Native.NOTIFYICONDATA Data() => new()
    {
        cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(), hWnd = _window.Handle,
        uID = 1, uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP,
        uCallbackMessage = Native.WM_TRAYICON, hIcon = _icon,
        szTip = "WeChat-Notifier — " + (Paused ? "已暂停" : _state),
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
    private void Update()
    {
        if (!_added) return;
        var data = Data();
        if (!Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref data))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "更新托盘状态失败。");
    }

    private nint? OnMessage(uint message, nint wParam, nint lParam)
    {
        if (message == _taskbarCreated && _taskbarCreated != 0) { _added = false; Add(); return 0; }
        if (message != Native.WM_TRAYICON) return null;
        var gesture = (int)((long)lParam & 0xffff);
        if (gesture is Native.WM_LBUTTONDBLCLK or 0x0400) SettingsRequested?.Invoke();
        else if (gesture is Native.WM_RBUTTONUP or 0x007B) ShowMenu();
        return 0;
    }

    private void ShowMenu()
    {
        var menu = Native.CreatePopupMenu();
        if (menu == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            void Item(int id, string label) { if (!Native.AppendMenu(menu, Native.MF_STRING, id, label)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
            Item(1, "设置…"); Item(2, Paused ? "恢复通知" : "暂停通知"); Item(3, "发送测试通知");
            Native.AppendMenu(menu, Native.MF_SEPARATOR, 0, null!);
            Item(4, "重新加载配置"); Item(5, "打开配置文件"); Item(6, "打开日志目录");
            Native.AppendMenu(menu, Native.MF_SEPARATOR, 0, null!);
            Item(7, "退出");
            if (!Native.GetCursorPos(out var point)) throw new Win32Exception(Marshal.GetLastWin32Error());
            Native.SetForegroundWindow(_window.Handle);
            var command = Native.TrackPopupMenuEx(menu, Native.TPM_RETURNCMD | Native.TPM_NONOTIFY | Native.TPM_RIGHTBUTTON,
                point.X, point.Y, _window.Handle, 0);
            Native.PostMessage(_window.Handle, Native.WM_NULL, 0, 0);
            switch (command)
            {
                case 1: SettingsRequested?.Invoke(); break;
                case 2: TogglePauseRequested?.Invoke(); break;
                case 3: TestRequested?.Invoke(); break;
                case 4: ReloadRequested?.Invoke(); break;
                case 5: ShellOpen.File(AppPaths.ConfigFile); break;
                case 6: ShellOpen.Folder(AppPaths.LogDirectory); break;
                case 7: ExitRequested?.Invoke(); break;
            }
        }
        finally { Native.DestroyMenu(menu); }
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = Data();
            if (!Native.Shell_NotifyIcon(Native.NIM_DELETE, ref data))
                Log.Warn("移除托盘图标失败：" + Marshal.GetLastWin32Error());
            _added = false;
        }
        _window.Dispose();
        Native.DestroyIcon(_icon);
    }
}
