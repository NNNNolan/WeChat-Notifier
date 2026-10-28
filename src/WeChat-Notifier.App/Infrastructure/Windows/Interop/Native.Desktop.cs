using System.Runtime.InteropServices;

namespace WeChat_Notifier.Infrastructure.Windows.Interop;

public static partial class Native
{
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_EX_LAYERED = 0x00080000;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_MOUSELEAVE = 0x02A3;
    public const uint WM_MOUSEACTIVATE = 0x0021;
    public const int MA_NOACTIVATE = 3;

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct WNDCLASSEX
    {
        public uint Size, Style;
        public nint WindowProc;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public char* MenuName;
        public char* ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TRACKMOUSEEVENT
    {
        public uint Size, Flags;
        public nint Hwnd;
        public uint HoverTime;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct BLENDFUNCTION
    {
        public byte Operation, Flags, ConstantAlpha, AlphaFormat;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    internal static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    internal static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "LoadCursorW")]
    internal static extern nint LoadCursor(nint instance, nint cursorName);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tracking);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int MessageBox(nint hwnd, string text, string caption, uint type);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetWinEventHook(uint firstEvent, uint lastEvent, nint module,
        nint callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnhookWinEvent(nint hook);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetThreadPreferredUILanguages(uint flags, string languages, out uint count);
    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UpdateLayeredWindow(nint hwnd, nint destinationDc, ref POINT destination,
        ref SIZE size, nint sourceDc, ref POINT source, uint colorKey, ref BLENDFUNCTION blend, uint flags);
    [DllImport("user32.dll")]
    internal static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")]
    internal static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")]
    internal static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")]
    internal static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint CreateDIBSection(nint dc, ref BITMAPINFO info, uint usage,
        out nint bits, nint section, uint offset);

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFO
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int XPels, YPels;
        public uint Used, Important, Colors;
    }

    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    internal static extern bool PlaySound(string sound, nint module, uint flags);
}
