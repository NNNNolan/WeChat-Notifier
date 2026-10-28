using System.ComponentModel;
using System.Runtime.InteropServices;
using WeChat_Notifier.Infrastructure.Windows.Interop;

namespace WeChat_Notifier.Infrastructure.Windows;

public static class ScreenHelper
{
    public sealed record Target(Native.RECT WorkPhysical, double Scale, nint Monitor);

    public static Target Resolve(string mode)
    {
        nint monitor;
        if (mode == "Mouse")
        {
            if (!Native.GetCursorPos(out var point)) throw new Win32Exception(Marshal.GetLastWin32Error());
            monitor = Native.MonitorFromPoint(point, Native.MONITOR_DEFAULTTONEAREST);
        }
        else monitor = Native.MonitorFromPoint(default, Native.MONITOR_DEFAULTTOPRIMARY);
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (monitor == 0 || !Native.GetMonitorInfo(monitor, ref info))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取显示器工作区。");
        Marshal.ThrowExceptionForHR(Native.GetDpiForMonitor(monitor, 0, out var dpi, out _));
        if (dpi == 0) throw new InvalidOperationException("显示器返回了无效的 DPI。");
        return new Target(info.rcWork, dpi / 96.0, monitor);
    }
}
