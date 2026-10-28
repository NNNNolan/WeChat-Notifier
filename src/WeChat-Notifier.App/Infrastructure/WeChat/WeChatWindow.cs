using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WeChat_Notifier.Configuration;
using WeChat_Notifier.Domain;
using WeChat_Notifier.Infrastructure.Logging;
using WeChat_Notifier.Infrastructure.Windows.Interop;

namespace WeChat_Notifier.Infrastructure.WeChat;

/// <summary>
/// Brings WeChat forward the way WeChat itself would.
///
/// The three window states need three different things, and getting this wrong
/// is very visible:
///
///   minimised  -> SW_RESTORE. WeChat still considers a minimised window
///                 "shown", so its show/hide hotkey would HIDE it instead.
///   in tray    -> send WeChat's own hotkey. Do NOT force SW_SHOW: WeChat's
///                 state machine still believes the window is hidden, so the
///                 window paints but never accepts input. It looks frozen and
///                 cannot be operated. Reported exactly that way on a real
///                 machine. Sending the hotkey runs WeChat's own code path,
///                 which shows the window properly.
///   on screen  -> ordinary focus request.
///
/// The hotkey is a toggle, so it is only ever sent for the tray case.
/// </summary>
public static class WeChatWindow
{
    internal static readonly string[] ProcessNames = ["Weixin", "WeChat", "WeChatApp"];

    private static HashSet<uint> GetPids()
    {
        var set = new HashSet<uint>();
        foreach (var name in ProcessNames)
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                foreach (var process in processes) set.Add((uint)process.Id);
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        return set;
    }

    public static bool IsRunning() => GetPids().Count > 0;

    public static bool ActivateOrStart(AppConfig config)
    {
        if (IsRunning()) return Activate(config.WeChatHotkey);
        if (string.IsNullOrWhiteSpace(config.WeChatPath))
            throw new InvalidOperationException("微信尚未运行，请在设置中填写微信启动路径。");
        if (!File.Exists(config.WeChatPath))
            throw new FileNotFoundException("微信启动路径不存在，请在设置中检查路径。", config.WeChatPath);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = config.WeChatPath,
            UseShellExecute = true
        });
        if (process == null) throw new InvalidOperationException("Windows 未能启动微信。");
        Log.Info("WeChat started from notification activation");
        return true;
    }

    /// <summary>
    /// Finds WeChat's conversation window. Prefers the exact title, and otherwise
    /// takes the largest window that is not the preferences dialog.
    /// </summary>
    internal static bool IsCandidateWindowClass(string name) =>
        name == "WeChatMainWndForPC"
        || name.StartsWith("Qt", StringComparison.Ordinal) && name.EndsWith("QWindowIcon", StringComparison.Ordinal);

    public static IntPtr Find() => Find(GetPids());

    internal static IntPtr Find(IReadOnlySet<uint> pids)
    {
        if (pids.Count == 0) return IntPtr.Zero;

        IntPtr exact = IntPtr.Zero;
        IntPtr largest = IntPtr.Zero;
        long largestArea = 0;

        if (!Native.EnumWindows((h, _) =>
        {
            Native.GetWindowThreadProcessId(h, out var pid);
            if (!pids.Contains(pid)) return true;
            if (!IsCandidateWindowClass(Native.GetClassNameOf(h))) return true;

            var title = Native.GetWindowTitleOf(h);
            if (title is "微信" or "WeChat" or "Weixin")
            {
                exact = h;
                return true;
            }
            if (title is "设置" or "Settings" or "WxTrayIconMessageWindow") return true;

            if (Native.GetWindowRect(h, out var r))
            {
                long area = (long)r.Width * r.Height;
                if (area > largestArea)
                {
                    largestArea = area;
                    largest = h;
                }
            }
            return true;
        }, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法枚举微信窗口。");

        return exact != IntPtr.Zero ? exact : largest;
    }

    internal static bool WaitForForeground(IntPtr hwnd, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (Native.GetForegroundWindow() == hwnd) return true;
            Thread.Sleep(20);
        }
        return Native.GetForegroundWindow() == hwnd;
    }

    internal static bool WaitForVisible(IntPtr hwnd, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (Native.IsWindowVisible(hwnd)) return true;
            Thread.Sleep(30);
        }
        return Native.IsWindowVisible(hwnd);
    }

    public static bool Activate(string? hotkey)
    {
        var hwnd = Find();
        if (hwnd == IntPtr.Zero)
        {
            Log.Warn("bring WeChat forward: no WeChat window found (is it running?)");
            return false;
        }

        try
        {
            if (Native.IsIconic(hwnd))
            {
                Native.ShowWindow(hwnd, Native.SW_RESTORE);
                var focused = WaitForForeground(hwnd, 400);
                Log.Info(focused ? "WeChat restored from the taskbar"
                                 : "WeChat restored but did not take focus");
                return focused;
            }

            if (!Native.IsWindowVisible(hwnd))
            {
                if (!HotkeyParser.TryParse(hotkey, out var parsed))
                {
                    Log.Warn($"cannot raise WeChat from the tray: no usable hotkey (configured: '{hotkey}')");
                    return false;
                }

                if (!Keyboard.Chord(parsed.Modifiers, parsed.Key)) return false;
                Log.Info($"sent {parsed.Display} to WeChat");

                if (WaitForVisible(hwnd, 2000))
                {
                    Log.Info("WeChat opened via its own hotkey");
                    return true;
                }

                Log.Warn($"WeChat did not appear after {parsed.Display} — check " +
                         "微信 → 设置 → 快捷键: the hotkey may have changed or been turned off");
                return false;
            }

            // Already on screen, merely behind something: an ordinary focus request.
            Native.SetForegroundWindow(hwnd);
            if (!WaitForForeground(hwnd, 300)) Native.SwitchToThisWindow(hwnd, true);
            var ok = WaitForForeground(hwnd, 400);
            Log.Info(ok ? "WeChat brought to the front" : "could not give WeChat the foreground");
            return ok;
        }
        catch (Exception ex)
        {
            Log.Warn("bring WeChat forward failed: " + ex.Message);
            return false;
        }
    }
}
