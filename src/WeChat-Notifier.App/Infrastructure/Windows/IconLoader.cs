using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WeChat_Notifier.Infrastructure.Windows.Interop;

namespace WeChat_Notifier.Infrastructure.Windows;

internal static class IconLoader
{
    public static string ImagePath => Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");

    public static nint LoadTrayIcon(int size = 0)
    {
        if (size == 0) size = Native.GetSystemMetrics(Native.SM_CXSMICON);
        var icon = Native.LoadImage(0, Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico"),
            Native.IMAGE_ICON, size, size, Native.LR_LOADFROMFILE);
        if (icon == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法加载程序图标。");
        return icon;
    }

    public static string? FindWeChatExePath()
    {
        foreach (var name in new[] { "Weixin", "WeChat" })
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        if (process.MainModule?.FileName is { } path && File.Exists(path)) return path;
                    }
                    catch (Win32Exception) { /* 已退出或受保护的微信进程不可读取。 */ }
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        return null;
    }
}
