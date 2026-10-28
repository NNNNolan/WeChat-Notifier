using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WeChat_Notifier.Infrastructure.Logging;
using WeChat_Notifier.Infrastructure.Windows;

namespace WeChat_Notifier.Infrastructure.Windows.Interop;

/// <summary>托盘和透明气泡共用的 Win32 窗口；回调为静态 unmanaged 入口，可直接 AOT。</summary>
internal sealed unsafe class NativeWindow : IDisposable
{
    private const string ClassName = "WeChatNotifier.Native";
    private static readonly Dictionary<nint, NativeWindow> Windows = new();
    private static bool _registered;
    public nint Handle { get; private set; }
    public Func<uint, nint, nint, nint?>? MessageHandler { get; set; }

    public NativeWindow(string title, uint style = 0, uint extendedStyle = Native.WS_EX_TOOLWINDOW)
    {
        if (!_registered)
        {
            var windowClass = new Native.WNDCLASSEX
            {
                Size = (uint)sizeof(Native.WNDCLASSEX),
                Instance = Native.GetModuleHandle(null),
                WindowProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&WindowProc,
                Cursor = Native.LoadCursor(0, (nint)32512)
            };
            fixed (char* name = ClassName)
            {
                windowClass.ClassName = name;
                if (Native.RegisterClassEx(ref windowClass) == 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "注册通知窗口失败。");
            }
            _registered = true;
        }
        Handle = Native.CreateWindowEx(extendedStyle, ClassName, title, style,
            -32000, -32000, 0, 0, 0, 0, Native.GetModuleHandle(null), 0);
        if (Handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "创建通知窗口失败。");
        Windows.Add(Handle, this);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try
        {
            if (Windows.TryGetValue(hwnd, out var window)
                && window.MessageHandler?.Invoke(message, wParam, lParam) is { } result)
                return result;
        }
        catch (Exception ex)
        {
            Log.Error("原生窗口事件失败", ex);
            App.ShowError("通知窗口错误", ex);
        }
        return Native.DefWindowProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (Handle == 0) return;
        if (!Native.DestroyWindow(Handle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "释放通知窗口失败。");
        Windows.Remove(Handle);
        Handle = 0;
    }
}
