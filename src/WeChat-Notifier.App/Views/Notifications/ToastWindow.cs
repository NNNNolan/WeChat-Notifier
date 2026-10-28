using Microsoft.UI.Dispatching;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.UI.ViewManagement;
using WeChat_Notifier.Configuration;
using WeChat_Notifier.Domain;
using WeChat_Notifier.Infrastructure.Windows.Interop;
using WeChat_Notifier.Infrastructure.Windows;

namespace WeChat_Notifier.Views.Notifications;

/// <summary>无激活透明气泡；玻璃算法沿用原项目，平台窗口与计时器替换为 Win32/WinUI。</summary>
internal sealed class ToastWindow : IDisposable
{
    private readonly AppConfig _config;
    private readonly NativeWindow _window;
    private readonly DispatcherQueueTimer _life, _motion, _backdrop;
    private readonly bool _pinned;
    private readonly bool _transient;
    private readonly bool _animations = new UISettings().AnimationsEnabled;
    private byte[] _raw = [], _previous = [], _work = [], _glass = [], _pixels = [];
    private double _scale, _top, _fromTop, _toTop;
    private int _left, _width, _height, _pad;
    private long _enterAt, _moveAt, _exitAt;
    private bool _shown, _hovered, _menuOpen, _excluded, _closing, _disposed;
    private bool? _lightText;
    public SessionInfo Session { get; }
    public bool IsClosing => _closing;
    public int PhysicalWidth => _width + _pad * 2;
    public int PhysicalHeight => _height + _pad * 2;
    public event Action<ToastWindow>? Finished;
    public Action<SessionInfo>? ActivateRequested { get; set; }
    public Action<TimeSpan?>? SuppressNotificationsRequested { get; set; }

    public ToastWindow(AppConfig config, SessionInfo session, DispatcherQueue dispatcher, double scale,
        bool pinned = false, bool transient = false)
    {
        _config = config;
        Session = session;
        _pinned = pinned;
        _transient = transient;
        _scale = scale;
        _window = new NativeWindow("WeChat-Notifier 气泡", Native.WS_POPUP,
            Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
        _window.MessageHandler = OnMessage;
        _life = dispatcher.CreateTimer();
        _life.IsRepeating = false;
        _life.Tick += (_, _) => Finish();
        _motion = dispatcher.CreateTimer();
        _motion.Interval = TimeSpan.FromMilliseconds(15);
        _motion.Tick += (_, _) => Animate();
        _backdrop = dispatcher.CreateTimer();
        _backdrop.Tick += (_, _) => RefreshBackdrop();
        Resize();
    }

    private void Resize()
    {
        _width = (int)Math.Round(_config.Width * _scale);
        _height = BubbleBitmap.Height(_config, Session, _scale);
        _pad = (int)Math.Round(BubbleBitmap.ShadowPad * _scale);
        _raw = new byte[_width * _height * 4];
        _previous = new byte[_raw.Length];
        _work = new byte[_raw.Length];
        _glass = new byte[_raw.Length];
    }

    public void Place(int left, int top, double scale, bool animate)
    {
        if (_disposed || _closing) return;
        if (Math.Abs(scale - _scale) > 0.001) { _scale = scale; Resize(); }
        _left = left;
        _toTop = top;
        if (animate && _shown && _animations && Math.Abs(_top - top) >= 1)
        {
            _fromTop = _top;
            _moveAt = Stopwatch.GetTimestamp();
            _motion.Start();
        }
        else _top = top;
        if (!_shown)
        {
            SetCaptureExclusion(_config.GlassBackdrop && _config.DynamicBackdrop);
            Render(force: true);
            _enterAt = Stopwatch.GetTimestamp();
            _shown = true;
            Present((byte)(_animations ? 0 : 255), _animations ? 48 * _scale : 0);
            Native.ShowWindow(_window.Handle, Native.SW_SHOWNOACTIVATE);
            if (_animations) _motion.Start();
            ArmLife();
        }
        else
        {
            Render(force: true);
            Present(255, 0);
        }
        ArmBackdrop();
    }

    public void ReapplyAppearance()
    {
        if (_disposed || _closing) return;
        Resize();
        SetCaptureExclusion(_config.GlassBackdrop && _config.DynamicBackdrop);
        Render(force: true);
        Present(255, 0);
        ArmBackdrop();
    }

    private void SetCaptureExclusion(bool exclude)
    {
        if (_excluded == exclude) return;
        if (!Native.SetWindowDisplayAffinity(_window.Handle, exclude ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法设置气泡背景采样隔离。");
        _excluded = exclude;
    }

    private void Render(bool force)
    {
        if (_config.GlassBackdrop)
        {
            var temporaryExclusion = _shown && !_excluded;
            if (temporaryExclusion) SetCaptureExclusion(true);
            try
            {
                if (!LiquidGlassRenderer.Capture(_left + _pad, (int)Math.Round(_top) + _pad, _width, _height, ref _raw))
                    throw new InvalidOperationException("无法采样气泡背后的桌面画面，请检查屏幕采集权限。");
            }
            finally { if (temporaryExclusion) SetCaptureExclusion(false); }
            if (!force && _raw.AsSpan().SequenceEqual(_previous)) return;
            var settings = new LiquidGlassRenderer.Settings
            {
                Thickness = _config.GlassThickness, Blur = _config.GlassBlur,
                RefractiveIndex = _config.GlassRefractiveIndex, ChromaticAberration = _config.GlassChromaticAberration,
                LightIntensity = _config.GlassLightIntensity, AmbientStrength = _config.GlassAmbientStrength,
                Saturation = _config.GlassSaturation, Tint = _config.GlassTint,
                RefractScale = _config.GlassRefractScale, LightAngleDeg = _config.GlassLightAngle,
                CornerRadiusDip = _config.CornerRadius, Dpr = _scale
            };
            var luma = LiquidGlassRenderer.RenderInto(_raw, _work, _glass, _width, _height, settings);
            _lightText = _lightText switch
            {
                null => luma < 0.55, true when luma > 0.62 => false, false when luma < 0.48 => true, _ => _lightText
            };
            Buffer.BlockCopy(_raw, 0, _previous, 0, _raw.Length);
        }
        else
        {
            _glass = BubbleBitmap.Solid(_config, _width, _height, _scale);
            _lightText = _config.Theme == "Dark";
        }
        _pixels = BubbleBitmap.Compose(_config, Session, _scale, _glass, _width, _height, _lightText == true, _hovered);
    }

    private void ArmLife()
    {
        if (_pinned || (!_transient && (_hovered || _menuOpen)) || _closing) return;
        // 1.3 s before the existing 0.2 s close animation keeps mode hints within 1.5 s.
        _life.Interval = TimeSpan.FromMilliseconds(_transient ? 1300 : _config.DurationMs);
        _life.Start();
    }

    private void ArmBackdrop()
    {
        _backdrop.Stop();
        if (!_config.GlassBackdrop || !_config.DynamicBackdrop || _closing) return;
        _backdrop.Interval = TimeSpan.FromMilliseconds(_config.BackdropRefreshMs);
        _backdrop.Start();
    }

    private void RefreshBackdrop()
    {
        try { Render(force: false); Present(255, 0); }
        catch (Exception ex)
        {
            _backdrop.Stop();
            App.ShowError("气泡背景刷新失败", ex);
        }
    }

    private void Animate()
    {
        if (_disposed) return;
        var now = Stopwatch.GetTimestamp();
        static double Progress(long now, long start, double ms) =>
            Math.Clamp((now - start) * 1000.0 / Stopwatch.Frequency / ms, 0, 1);
        if (_closing)
        {
            var progress = _animations ? Progress(now, _exitAt, 200) : 1;
            Present((byte)Math.Round(255 * (1 - progress * progress * progress)), 48 * _scale * progress);
            if (progress >= 1) Dispose();
            return;
        }
        var enter = Progress(now, _enterAt, 320);
        var move = _moveAt == 0 ? 1 : Progress(now, _moveAt, 260);
        if (move < 1) _top = _fromTop + (_toTop - _fromTop) * (1 - Math.Pow(1 - move, 3));
        else _top = _toTop;
        Present((byte)Math.Round(255 * (1 - Math.Pow(1 - enter, 3))), 48 * _scale * Math.Pow(1 - enter, 3));
        if (enter >= 1 && move >= 1)
        {
            _motion.Stop();
            Render(force: true);
            Present(255, 0);
        }
    }

    private void Present(byte alpha, double slide)
    {
        if (_pixels.Length == 0) return;
        var dc = Native.CreateCompatibleDC(0);
        if (dc == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        nint bitmap = 0, old = 0;
        try
        {
            var info = new Native.BITMAPINFO
            {
                Size = 40, Width = PhysicalWidth, Height = -PhysicalHeight, Planes = 1, BitCount = 32
            };
            bitmap = Native.CreateDIBSection(dc, ref info, 0, out var bits, 0, 0);
            if (bitmap == 0 || bits == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            old = Native.SelectObject(dc, bitmap);
            Marshal.Copy(_pixels, 0, bits, _pixels.Length);
            var destination = new Native.POINT { X = _left + (int)Math.Round(slide), Y = (int)Math.Round(_top) };
            var source = new Native.POINT();
            var size = new Native.SIZE { cx = PhysicalWidth, cy = PhysicalHeight };
            var blend = new Native.BLENDFUNCTION { ConstantAlpha = alpha, AlphaFormat = 1 };
            if (!Native.UpdateLayeredWindow(_window.Handle, 0, ref destination, ref size, dc, ref source, 0, ref blend, 2))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "更新透明气泡失败。");
            if (!Native.SetWindowPos(_window.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            if (old != 0) Native.SelectObject(dc, old);
            if (bitmap != 0) Native.DeleteObject(bitmap);
            Native.DeleteDC(dc);
        }
    }

    private nint? OnMessage(uint message, nint wParam, nint lParam)
    {
        if (message == Native.WM_MOUSEACTIVATE) return Native.MA_NOACTIVATE;
        if (message == Native.WM_MOUSEMOVE && !_hovered)
        {
            _hovered = true;
            if (!_transient) _life.Stop();
            var tracking = new Native.TRACKMOUSEEVENT { Size = (uint)Marshal.SizeOf<Native.TRACKMOUSEEVENT>(), Flags = 2, Hwnd = _window.Handle };
            if (!Native.TrackMouseEvent(ref tracking)) throw new Win32Exception(Marshal.GetLastWin32Error());
            Render(force: true);
            Present(255, 0);
            return 0;
        }
        if (message == Native.WM_MOUSELEAVE)
        {
            _hovered = false;
            if (!_transient) ArmLife();
            Render(force: true);
            Present(255, 0);
            return 0;
        }
        if (message == Native.WM_LBUTTONUP && !_closing)
        {
            var x = (short)((long)lParam & 0xffff);
            var y = (short)(((long)lParam >> 16) & 0xffff);
            var close = x >= _pad + _width - 34 * _scale && x <= _pad + _width - 8 * _scale
                && y >= _pad + 8 * _scale && y <= _pad + 34 * _scale;
            if (close) Finish();
            else
            {
                if (!_transient) ActivateRequested?.Invoke(Session);
                if (!_pinned) Finish();
            }
            return 0;
        }
        if (message == Native.WM_RBUTTONUP && !_pinned && !_transient && !_closing) { ShowMenu(); return 0; }
        return null;
    }

    private void ShowMenu()
    {
        var menu = Native.CreatePopupMenu();
        if (menu == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        _menuOpen = true;
        _life.Stop();
        try
        {
            Native.AppendMenu(menu, Native.MF_STRING, 1, "关闭通知");
            Native.AppendMenu(menu, Native.MF_STRING, 2, "暂时关闭通知");
            Native.GetCursorPos(out var point);
            var choice = Native.TrackPopupMenuEx(menu, Native.TPM_RETURNCMD | Native.TPM_NONOTIFY | Native.TPM_RIGHTBUTTON,
                point.X, point.Y, _window.Handle, 0);
            if (choice is 1 or 2)
            {
                SuppressNotificationsRequested?.Invoke(choice == 1 ? null : TimeSpan.FromMinutes(1.314));
                Finish();
            }
        }
        finally { Native.DestroyMenu(menu); _menuOpen = false; ArmLife(); }
    }

    public void Finish()
    {
        if (_closing || _disposed) return;
        _closing = true;
        _life.Stop();
        _backdrop.Stop();
        _exitAt = Stopwatch.GetTimestamp();
        if (_animations) _motion.Start();
        else Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _life.Stop();
        _motion.Stop();
        _backdrop.Stop();
        _window.Dispose();
        Finished?.Invoke(this);
    }
}
