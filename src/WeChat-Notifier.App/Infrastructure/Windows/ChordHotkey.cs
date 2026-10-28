using System.ComponentModel;
using System.Runtime.InteropServices;
using WeChat_Notifier.Infrastructure.Windows.Interop;

namespace WeChat_Notifier.Infrastructure.Windows;

/// <summary>仅为双主键快捷键监听目标标点和修饰键，不收集或记录其他按键。</summary>
internal sealed class ChordHotkey : IDisposable
{
    private static readonly ushort[] WatchedKeys = [0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C, 0xBC, 0xBE];
    private readonly HashSet<ushort> _down = new(16);
    private readonly HotkeyParser.Parsed _chord;
    private readonly uint _modifiers;
    private readonly Action _trigger;
    private readonly Action<Exception> _failed;
    private readonly HookProc _callback;
    private nint _hook;
    private bool _active;

    internal ChordHotkey(HotkeyParser.Parsed chord, Action trigger, Action<Exception> failed)
    {
        _chord = chord;
        _modifiers = GlobalHotkeys.Modifiers(chord);
        _trigger = trigger;
        _failed = failed;
        _callback = OnKeyboard;
        foreach (var key in WatchedKeys)
            if ((Native.GetAsyncKeyState(key) & 0x8000) != 0) _down.Add(key);
        _active = RequiredKeysHeld();
        _hook = SetWindowsHookEx(13, _callback, Native.GetModuleHandle(null), 0);
        if (_hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法监听多键模式快捷键。");
    }

    private uint CurrentModifiers() =>
        (Down(0xA4, 0xA5) ? 1u : 0) | (Down(0xA2, 0xA3) ? 2u : 0)
        | (Down(0xA0, 0xA1) ? 4u : 0) | (Down(0x5B, 0x5C) ? 8u : 0);

    private bool Down(ushort left, ushort right) => _down.Contains(left) || _down.Contains(right);

    private bool RequiredKeysHeld() => _down.Contains(_chord.Key) && _down.Contains(_chord.AdditionalKey)
        && (CurrentModifiers() & _modifiers) == _modifiers;

    private nint OnKeyboard(int code, nint message, nint data)
    {
        if (code >= 0)
        {
            try
            {
                var key = (ushort)Marshal.ReadInt32(data);
                key = key switch { 0x10 => 0xA0, 0x11 => 0xA2, 0x12 => 0xA4, _ => key };
                if (Array.IndexOf(WatchedKeys, key) >= 0)
                {
                    var pressed = message is 0x0100 or 0x0104;
                    if (pressed) _down.Add(key); else _down.Remove(key);
                    if (!RequiredKeysHeld()) _active = false;
                    if (pressed && RequiredKeysHeld() && CurrentModifiers() == _modifiers)
                    {
                        if (!_active) { _active = true; _trigger(); }
                        if (key == _chord.Key || key == _chord.AdditionalKey) return 1;
                    }
                }
            }
            catch (Exception ex) { _failed(ex); }
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    public void Dispose()
    {
        if (_hook == 0) return;
        if (!UnhookWindowsHookEx(_hook)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法释放多键模式快捷键。");
        _hook = 0;
        _down.Clear();
        _active = false;
        GC.KeepAlive(_callback);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint HookProc(int code, nint message, nint data);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int hook, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
}
