using Microsoft.UI.Dispatching;
using WeChat_Notifier.Configuration;
using WeChat_Notifier.Domain;
using WeChat_Notifier.Infrastructure.Windows;
using WeChat_Notifier.Views.Notifications;

namespace WeChat_Notifier.Application.Notifications;

internal sealed class ToastManager : IDisposable
{
    private readonly AppConfig _config;
    private readonly DispatcherQueue _dispatcher;
    private readonly List<ToastWindow> _toasts = [];
    private readonly DispatcherQueueTimer _previewDebounce;
    private ToastWindow? _preview;
    private ToastWindow? _status;
    public AppConfig LayoutConfig { get; set; }
    public Action<SessionInfo>? ActivateChat { get; set; }
    public event Action<TimeSpan?>? SuppressNotificationsRequested;
    public event Action? Changed;
    public int ActiveCount => _toasts.Count;

    public ToastManager(AppConfig config, DispatcherQueue dispatcher)
    {
        _config = config;
        _dispatcher = dispatcher;
        _previewDebounce = dispatcher.CreateTimer();
        LayoutConfig = config;
    }

    public void Show(SessionInfo session) => Add(_config, session, false);

    public void ShowStatus(string title, string body)
    {
        DismissStatus();
        _status = Add(_config, new SessionInfo
        {
            Key = "__status__", Title = title, Preview = body, Time = DateTime.Now.ToString("HH:mm")
        }, pinned: false, transient: true);
    }

    private ToastWindow Add(AppConfig options, SessionInfo session, bool pinned, bool transient = false)
    {
        var toast = new ToastWindow(options, session, _dispatcher, ScreenHelper.Resolve(LayoutConfig.Monitor).Scale, pinned, transient)
        {
            ActivateRequested = s => ActivateChat?.Invoke(s),
            SuppressNotificationsRequested = duration => SuppressNotificationsRequested?.Invoke(duration)
        };
        toast.Finished += OnFinished;
        _toasts.Insert(0, toast);
        if (pinned) _preview = toast;
        while (_toasts.Count > LayoutConfig.MaxVisible)
        {
            var oldest = _toasts[^1];
            _toasts.RemoveAt(_toasts.Count - 1);
            if (ReferenceEquals(oldest, _preview)) _preview = null;
            if (ReferenceEquals(oldest, _status)) _status = null;
            oldest.Finished -= OnFinished;
            oldest.Dispose();
        }
        try { Relayout(toast); }
        catch { _toasts.Remove(toast); toast.Finished -= OnFinished; toast.Dispose(); throw; }
        Changed?.Invoke();
        return toast;
    }

    private void OnFinished(ToastWindow toast)
    {
        toast.Finished -= OnFinished;
        if (ReferenceEquals(_preview, toast)) _preview = null;
        if (ReferenceEquals(_status, toast)) _status = null;
        _toasts.Remove(toast);
        Relayout();
        Changed?.Invoke();
    }

    public void Relayout() => Relayout(null);

    private void Relayout(ToastWindow? added)
    {
        if (_toasts.Count == 0) return;
        var target = ScreenHelper.Resolve(LayoutConfig.Monitor);
        var work = target.WorkPhysical;
        var scale = target.Scale;
        var pad = BubbleBitmap.ShadowPad * scale;
        var top = LayoutConfig.Position.StartsWith("Top", StringComparison.Ordinal);
        var right = LayoutConfig.Position.EndsWith("Right", StringComparison.Ordinal);
        var cursor = top ? work.Top + LayoutConfig.MarginY * scale : work.Bottom - LayoutConfig.MarginY * scale;
        foreach (var toast in _toasts)
        {
            var height = toast.PhysicalHeight - 2 * pad;
            var x = right ? work.Right - LayoutConfig.MarginX * scale - toast.PhysicalWidth + pad
                : work.Left + LayoutConfig.MarginX * scale - pad;
            var y = top ? cursor - pad : cursor - height - pad;
            toast.Place((int)Math.Round(x), (int)Math.Round(y), scale, animate: !ReferenceEquals(toast, added));
            cursor += (height + LayoutConfig.Spacing * scale) * (top ? 1 : -1);
        }
    }

    public void ShowPreview(AppConfig working, SessionInfo? session = null)
    {
        if (_preview != null && session != null
            && (_preview.Session.Title != session.Title || _preview.Session.Preview != session.Preview))
            DismissPreview();
        LayoutConfig = working;
        if (_preview == null) Add(working, session ?? new SessionInfo
        {
            Key = "__preview__", Title = "微信通知预览",
            Preview = "这就是气泡的实际效果。\n修改左侧参数，预览会实时更新。", Time = DateTime.Now.ToString("HH:mm")
        }, true);
        _previewDebounce.Stop();
        _previewDebounce.Interval = TimeSpan.FromMilliseconds(40);
        _previewDebounce.IsRepeating = false;
        _previewDebounce.Tick -= RefreshPreview;
        _previewDebounce.Tick += RefreshPreview;
        _previewDebounce.Start();
    }

    private void RefreshPreview(DispatcherQueueTimer sender, object args)
    {
        try { _preview?.ReapplyAppearance(); Relayout(); }
        catch (Exception ex) { App.ShowError("无法更新气泡预览", ex); }
    }

    public void DismissPreview()
    {
        _previewDebounce.Stop();
        var preview = _preview;
        _preview = null;
        preview?.Dispose();
        LayoutConfig = _config;
        Relayout();
    }

    private void DismissStatus()
    {
        var status = _status;
        _status = null;
        status?.Dispose();
    }

    public void ClearAll()
    {
        _previewDebounce.Stop();
        _preview = null;
        _status = null;
        foreach (var toast in _toasts.ToArray()) { toast.Finished -= OnFinished; toast.Dispose(); }
        _toasts.Clear();
        LayoutConfig = _config;
        Changed?.Invoke();
    }

    public void Dispose() => ClearAll();
}
