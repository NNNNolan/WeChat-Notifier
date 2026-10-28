using System.Runtime.InteropServices;
using WeChat_Notifier.Infrastructure.Logging;
using WeChat_Notifier.Infrastructure.Windows;

namespace WeChat_Notifier.Infrastructure.Windows.Interop;

/// <summary>Synthesises keyboard chords, for driving another app's global hotkey.</summary>
public static class Keyboard
{
    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    public const ushort VK_SHIFT = 0x10;
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_MENU = 0x12;   // Alt
    public const ushort VK_LWIN = 0x5B;

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = up ? KEYEVENTF_KEYUP : 0,
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        }
    };

    /// <summary>
    /// Presses every modifier, taps <paramref name="key"/>, then releases the
    /// modifiers in reverse.
    ///
    /// The stages are sent as separate calls with a small gap rather than one
    /// zero-delay batch. Measured against WeChat: a single burst was silently
    /// dropped the first time it was tried, while spaced presses toggled the
    /// window every time. Real hardware never presses a chord on the same
    /// microsecond, and hotkey detection often samples rather than queues.
    /// </summary>
    public static bool Chord(IReadOnlyList<ushort> modifiers, ushort key, int gapMs = 18, ushort additionalKey = 0)
    {
        var keys = new List<ushort>(modifiers) { key };
        if (additionalKey != 0) keys.Add(additionalKey);
        var pressed = new List<ushort>();
        var success = true;
        try
        {
            foreach (var value in keys)
            {
                if (!Send(Key(value, up: false))) { success = false; break; }
                pressed.Add(value);
                Thread.Sleep(gapMs);
            }
        }
        finally
        {
            // 发送失败也必须释放本次已按下的键，避免留下 Ctrl/Alt 卡住的状态。
            for (var i = pressed.Count - 1; i >= 0; i--)
            {
                if (!Send(Key(pressed[i], up: true))) success = false;
                Thread.Sleep(gapMs);
            }
        }
        return success;
    }

    private static bool Send(INPUT input)
    {
        var batch = new[] { input };
        var sent = SendInput(1, batch, Marshal.SizeOf<INPUT>());
        if (sent == 1) return true;
        Log.Warn($"SendInput dropped an event (last error {Marshal.GetLastWin32Error()})");
        return false;
    }
}

/// <summary>Parses a hotkey written the way Windows apps display it, e.g. "Ctrl+Alt+W".</summary>
public static class HotkeyParser
{
    public sealed record Parsed(IReadOnlyList<ushort> Modifiers, ushort Key, string Display, ushort AdditionalKey = 0);

    public static bool TryParse(string? text, out Parsed parsed)
    {
        parsed = new Parsed(Array.Empty<ushort>(), 0, "");

        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;   // a bare key cannot be a global hotkey

        var modifiers = new List<ushort>();
        ushort key = 0;
        ushort additionalKey = 0;
        var display = new List<string>();

        foreach (var raw in parts)
        {
            var p = raw.ToLowerInvariant();
            switch (p)
            {
                case "ctrl" or "control": modifiers.Add(Keyboard.VK_CONTROL); display.Add("Ctrl"); continue;
                case "alt": modifiers.Add(Keyboard.VK_MENU); display.Add("Alt"); continue;
                case "shift": modifiers.Add(Keyboard.VK_SHIFT); display.Add("Shift"); continue;
                case "win" or "windows" or "meta": modifiers.Add(Keyboard.VK_LWIN); display.Add("Win"); continue;
            }

            if (key != 0)
            {
                // 仅扩展逗号与句号这组双主键，其他主键仍只能选一个。
                if (additionalKey != 0 || !((key == 0xBC && p == ".") || (key == 0xBE && p == ","))) return false;
                var primaryIndex = display.IndexOf(key == 0xBC ? "," : ".");
                key = 0xBC;
                additionalKey = 0xBE;
                display[primaryIndex] = ",";
                display.Add(".");
                continue;
            }

            if (p.Length == 1 && p[0] is >= 'a' and <= 'z')
            {
                key = (ushort)char.ToUpperInvariant(p[0]);
                display.Add(char.ToUpperInvariant(p[0]).ToString());
            }
            else if (p.Length == 1 && p[0] is >= '0' and <= '9')
            {
                key = p[0];
                display.Add(p[0].ToString());
            }
            else if (p is "," or ".")
            {
                key = p == "," ? (ushort)0xBC : (ushort)0xBE;
                display.Add(p);
            }
            else if (p.StartsWith('f') && int.TryParse(p[1..], out var fn) && fn is >= 1 and <= 24)
            {
                key = (ushort)(0x70 + fn - 1);   // VK_F1 .. VK_F24
                display.Add(p.ToUpperInvariant());
            }
            else
            {
                return false;
            }
        }

        if (key == 0 || modifiers.Count == 0 || modifiers.Distinct().Count() != modifiers.Count) return false;

        parsed = new Parsed(modifiers, key, string.Join("+", display), additionalKey);
        return true;
    }
}
