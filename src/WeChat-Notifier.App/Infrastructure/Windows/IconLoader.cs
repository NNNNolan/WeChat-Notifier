using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using H.NotifyIcon;
using WeChat_Notifier.Domain;
using WeChat_Notifier.Infrastructure.Windows.Interop;

namespace WeChat_Notifier.Infrastructure.Windows;

internal static class IconLoader
{
    public static string ImagePath => Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
    private static string TrayPath => Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico");
    private const int GeneratedTrayIconSize = 256;

    public static nint LoadTrayIcon(int size = 0)
    {
        if (size == 0) size = Native.GetSystemMetrics(Native.SM_CXSMICON);
        var icon = Native.LoadImage(0, TrayPath,
            Native.IMAGE_ICON, size, size, Native.LR_LOADFROMFILE);
        if (icon == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法加载程序图标。");
        return icon;
    }

    internal static nint CreateTrayIcon(NotificationMode mode, int unreadCount)
    {
        const int size = GeneratedTrayIconSize;
        var pixels = RenderTrayPixels(mode, size);
        if (unreadCount <= 0)
            return GdiPlus.CreateIcon(pixels, size, size);

        var label = unreadCount > 99 ? "99+" : unreadCount.ToString();
        var badgeHeight = size * 0.66f;
        var badgeWidth = size * (label.Length switch { 1 => 0.66f, 2 => 0.80f, _ => 0.90f });
        var badgeLeft = size - badgeWidth;
        var fontSize = label.Length switch { 1 => 80f, 2 => 68f, _ => 48f };

        using var background = CreateBitmap(pixels, size);
        using var badgeBrush = new SolidBrush(Color.FromArgb(0xE5, 0x48, 0x4D));
        using var textBrush = new SolidBrush(Color.White);
        using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Point);
        using var iconBitmap = SystemDrawingIconGenerator.Generate(
            backgroundBrush: badgeBrush,
            foregroundBrush: textBrush,
            backgroundType: label.Length == 1 ? BackgroundType.Ellipse : BackgroundType.RoundedRectangle,
            cornerRadius: badgeHeight / 2,
            rectangle: new RectangleF(badgeLeft, 0, badgeWidth, badgeHeight),
            baseImage: background,
            size: size);
        using (var graphics = Graphics.FromImage(iconBitmap))
        using (var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.None
        })
        {
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.DrawString(label, font, textBrush,
                new RectangleF(badgeLeft, 0, badgeWidth, badgeHeight), format);
        }
        return iconBitmap.GetHicon();
    }

    private static Bitmap CreateBitmap(byte[] pixels, int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        try
        {
            var rectangle = new Rectangle(0, 0, size, size);
            var data = bitmap.LockBits(rectangle, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); }
            finally { bitmap.UnlockBits(data); }
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    internal static byte[] RenderTrayPixels(NotificationMode mode, int size = 32)
    {
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
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
