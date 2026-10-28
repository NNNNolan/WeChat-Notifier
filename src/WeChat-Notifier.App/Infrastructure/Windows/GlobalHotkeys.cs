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
    private bool _recording;
    private bool _disposed;
    public event Action<Exception>? Failed;
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
        if (_recording || !config.WeChatHotkeyEnabled)
        {
            Release();
            State = HotkeyState.Disabled;
            return;
        }
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

    public static bool IsAvailable(nint hwnd, string hotkey)
    {
        if (hwnd == 0 || !HotkeyParser.TryParse(hotkey, out var parsed)) return false;
        if (!Native.RegisterHotKey(hwnd, 0x7ffe, Modifiers(parsed), parsed.Key)) return false;
        if (!Native.UnregisterHotKey(hwnd, 0x7ffe)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return true;
    }

    private nint? OnMessage(uint message, nint wParam, nint lParam)
    {
        if (message != 0x0312 || wParam != 1 || _registered == null) return null;
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
        _window.Dispose();
        _disposed = true;
    }
}
