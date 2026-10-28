using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WeChat_Notifier.Domain;
using WeChat_Notifier.Infrastructure.Windows.Interop;

namespace WeChat_Notifier.Infrastructure.Windows;

internal static class IconLoader
{
    public static string ImagePath => Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
    private static string TrayPath => Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico");

    public static nint LoadTrayIcon(int size = 0)
    {
        if (size == 0) size = Native.GetSystemMetrics(Native.SM_CXSMICON);
        var icon = Native.LoadImage(0, TrayPath,
            Native.IMAGE_ICON, size, size, Native.LR_LOADFROMFILE);
        if (icon == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法加载程序图标。");
        return icon;
    }

    internal static nint CreateTrayIcon(NotificationMode mode, int unreadCount)
        => GdiPlus.CreateIcon(RenderTrayPixels(mode, unreadCount), 32, 32);

    internal static byte[] RenderTrayPixels(NotificationMode mode, int unreadCount)
    {
        const int size = 32;
        var pixels = new byte[size * size * 4];
        GdiPlus.Draw(pixels, size, size, graphics =>
            GdiPlus.Image(graphics, TrayPath, new(0, 0, size, size)));

        if (mode != NotificationMode.Normal)
        {
            var color = mode == NotificationMode.Privacy ? (r: (byte)0x4E, g: (byte)0x6E, b: (byte)0xF2)
                : (r: (byte)0x28, g: (byte)0x28, b: (byte)0x2B);
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var alpha = pixels[i + 3];
                if (alpha == 0) continue;
                // 只替换绿色图形；透明眼睛和气泡外形保持原样。
                if (pixels[i + 1] <= pixels[i + 2] || pixels[i + 1] <= pixels[i]) continue;
                pixels[i] = (byte)(color.b * alpha / 255);
                pixels[i + 1] = (byte)(color.g * alpha / 255);
                pixels[i + 2] = (byte)(color.r * alpha / 255);
            }
        }

        if (unreadCount > 0)
        {
            var label = unreadCount > 99 ? "99+" : unreadCount.ToString();
            GdiPlus.Draw(pixels, size, size, graphics =>
            {
                GdiPlus.Ellipse(graphics, new(17, 0, 15, 15), 0xFFE5484D);
                GdiPlus.Text(graphics, label, new(17, 1, 15, 13), label.Length > 2 ? 6 : 8,
                    true, 0xFFFFFFFF, true);
            });
        }
        return pixels;
    }

    internal static nint CreateBlankIcon() => GdiPlus.CreateIcon(new byte[32 * 32 * 4], 32, 32);

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
