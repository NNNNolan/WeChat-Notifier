using Microsoft.UI.Dispatching;
using System.ComponentModel;
using System.Runtime.InteropServices;
using WeChat_Notifier.Configuration;
using WeChat_Notifier.Domain;
using WeChat_Notifier.Infrastructure.Logging;
using WeChat_Notifier.Infrastructure.WeChat;
using WeChat_Notifier.Infrastructure.Windows.Interop;

namespace WeChat_Notifier.Infrastructure.Windows;

/// <summary>微信未运行时接管启动热键，运行后交回微信。所有状态修改位于 UI 线程。</summary>
internal sealed class GlobalHotkeys(AppConfig config, DispatcherQueue dispatcher, WeChatStateMonitor state) : IDisposable
{
    private readonly NativeWindow _window = new("WeChatNotifier.Hotkeys");
    private string? _registered;
    private string? _modeRegistered;
    private ChordHotkey? _modeChord;
    private string? _modeError;
    private bool _recording;
    private bool _disposed;
    public event Action<Exception>? Failed;
    public event Action? ModeSwitchRequested;
    internal nint Handle => _window.Handle;
    internal bool IsModeHotkeyRegistered => _modeRegistered != null;
    public string ModeStatusText => !config.ModeSwitchHotkeyEnabled ? "模式切换热键已停用"
        : _recording ? "正在录制快捷键"
        : _modeRegistered != null ? $"模式切换热键：{_modeRegistered}"
        : "模式切换热键不可用：" + _modeError;
    public enum HotkeyState { Disabled, HandedOver, Listening, Conflict }
    public HotkeyState State { get; private set; }
    public string StatusText => State switch
    {
        HotkeyState.HandedOver => "微信运行中，热键已交还微信",
        HotkeyState.Listening => "微信未运行，热键已接管",
        HotkeyState.Conflict => "热键冲突或格式无效",
        _ => "热键已停用"
    };

    public void Start()
    {
        _window.MessageHandler = OnMessage;
        state.Changed += ReRegister;
        Sync();
    }

    public void ReRegister() => Sync();
    public void SetRecording(bool recording) { _recording = recording; Sync(); }

    private void Sync()
    {
        if (_disposed) return;
        if (_registered != config.WeChatHotkey) Release();
        if (_modeRegistered != config.ModeSwitchHotkey) { ReleaseMode(); _modeError = null; }
        if (_recording || !config.WeChatHotkeyEnabled)
        {
            Release();
            State = HotkeyState.Disabled;
            SyncModeHotkey();
            return;
        }
        SyncModeHotkey();
        if (state.IsRunning)
        {
            Release();
            State = HotkeyState.HandedOver;
            return;
        }
        if (_registered != null) return;
        if (HotkeyParser.TryParse(config.WeChatHotkey, out var key)
            && Native.RegisterHotKey(_window.Handle, 1, Modifiers(key) | 0x4000, key.Key))
        {
            _registered = config.WeChatHotkey;
            State = HotkeyState.Listening;
            Log.Info("微信未运行，已接管热键：" + _registered);
            return;
        }
        if (State != HotkeyState.Conflict) Log.Warn("微信热键冲突或格式无效：" + config.WeChatHotkey);
        State = HotkeyState.Conflict;
    }

    private void Release()
    {
        if (_registered == null) return;
        if (!Native.UnregisterHotKey(_window.Handle, 1))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "释放微信热键失败。");
        _registered = null;
    }

    private void SyncModeHotkey()
    {
        if (_recording || !config.ModeSwitchHotkeyEnabled)
        {
            ReleaseMode();
            return;
        }
        if (_modeRegistered != null) return;
        if (HotkeyParser.TryParse(config.ModeSwitchHotkey, out var key))
        {
            if (key.AdditionalKey != 0)
            {
                ChordHotkey? registration = null;
                registration = new ChordHotkey(key, () =>
                {
                    if (!dispatcher.TryEnqueue(() =>
                        { if (!_disposed && !_recording && registration != null && ReferenceEquals(_modeChord, registration)) ModeSwitchRequested?.Invoke(); }))
                        throw new InvalidOperationException("UI 队列已关闭，无法切换模式。");
                }, error => dispatcher.TryEnqueue(() => Failed?.Invoke(error)));
                _modeChord = registration;
            }
            if (_modeChord != null || Native.RegisterHotKey(_window.Handle, 2, Modifiers(key) | 0x4000, key.Key))
            {
                _modeRegistered = config.ModeSwitchHotkey;
                _modeError = null;
                return;
            }
        }
        var error = "快捷键冲突或格式无效：" + config.ModeSwitchHotkey;
        if (_modeError != error) { _modeError = error; Log.Warn(error); }
    }

    internal void ValidateModeHotkey(AppConfig working)
    {
        if (!working.ModeSwitchHotkeyEnabled || SameHotkey(_modeRegistered, working.ModeSwitchHotkey)) return;
        if (!IsAvailable(_window.Handle, working.ModeSwitchHotkey))
            throw new InvalidOperationException("模式切换快捷键已被其他程序占用，请选择其他组合。");
    }

    private void ReleaseMode()
    {
        if (_modeRegistered == null) return;
        if (_modeChord != null) { _modeChord.Dispose(); _modeChord = null; }
        else if (!Native.UnregisterHotKey(_window.Handle, 2))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "释放模式切换热键失败。");
        _modeRegistered = null;
    }

    internal static uint Modifiers(HotkeyParser.Parsed key)
    {
        uint value = 0;
        foreach (var modifier in key.Modifiers)
            value |= modifier switch
            {
                Keyboard.VK_MENU => 1u, Keyboard.VK_CONTROL => 2u,
                Keyboard.VK_SHIFT => 4u, Keyboard.VK_LWIN => 8u, _ => 0u
            };
        return value;
    }

    internal static bool SameHotkey(string? first, string? second) =>
        HotkeyParser.TryParse(first, out var a) && HotkeyParser.TryParse(second, out var b)
        && a.Key == b.Key && a.AdditionalKey == b.AdditionalKey && Modifiers(a) == Modifiers(b);

    public static bool IsAvailable(nint hwnd, string hotkey)
    {
        if (hwnd == 0 || !HotkeyParser.TryParse(hotkey, out var parsed)) return false;
        if (parsed.AdditionalKey != 0)
        {
            // 多主键没有独占注册接口；这里只验证监听能否建立，不宣称可识别其他钩子的占用。
            Exception? callbackError = null;
            using (var probe = new ChordHotkey(parsed, () => { }, error => callbackError = error)) { }
            if (callbackError != null) throw new InvalidOperationException("验证模式快捷键监听失败。", callbackError);
            return true;
        }
        if (!Native.RegisterHotKey(hwnd, 0x7ffe, Modifiers(parsed), parsed.Key)) return false;
        if (!Native.UnregisterHotKey(hwnd, 0x7ffe)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return true;
    }

    private nint? OnMessage(uint message, nint wParam, nint lParam)
    {
        if (message != 0x0312) return null;
        if (wParam == 2 && _modeRegistered != null && _modeChord == null)
        {
            ModeSwitchRequested?.Invoke();
            return 0;
        }
        if (wParam != 1 || _registered == null) return null;
        _ = Task.Run(() =>
        {
            try
            {
                if (!WeChatWindow.ActivateOrStart(config))
                    throw new InvalidOperationException("无法启动微信，请检查微信启动路径。");
            }
            catch (Exception ex)
            {
                dispatcher.TryEnqueue(() => { if (!_disposed) Failed?.Invoke(ex); });
            }
        });
        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        state.Changed -= ReRegister;
        Release();
        ReleaseMode();
        _window.Dispose();
        _disposed = true;
    }
}
