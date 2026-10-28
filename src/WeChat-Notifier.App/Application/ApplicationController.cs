using Microsoft.UI.Dispatching;
using WeChat_Notifier.Application.Notifications;
using WeChat_Notifier.Configuration;
using WeChat_Notifier.Domain;
using WeChat_Notifier.Infrastructure.Logging;
using WeChat_Notifier.Infrastructure.Notifications;
using WeChat_Notifier.Infrastructure.WeChat.Database;
using WeChat_Notifier.Infrastructure.WeChat;
using WeChat_Notifier.Infrastructure.Windows.Interop;
using WeChat_Notifier.Infrastructure.Windows;
using WeChat_Notifier.Views.Notifications;
using WeChat_Notifier.Views;

namespace WeChat_Notifier.Application;

internal sealed class ApplicationController : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly WeChatStateMonitor _wechat;
    private readonly WeChatNotificationSource _source;
    private readonly ToastManager _bubbles;
    private readonly WindowsNotificationService _windows = new();
    private readonly NotificationPresenter _notifications;
    private readonly TrayIcon _tray = new();
    private readonly DispatcherQueueTimer _pauseTimer;
    private MainWindow? _settings;
    private bool _disposed;
    public AppConfig Config { get; }
    public GlobalHotkeys Hotkeys { get; }
    public string ConnectionText { get; private set; } = "等待微信";
    public bool Paused => _source.IsPaused;
    public event Action? StatusChanged;

    public ApplicationController(AppConfig config, DispatcherQueue dispatcher)
    {
        Config = config;
        _dispatcher = dispatcher;
        _wechat = new WeChatStateMonitor(dispatcher);
        _source = new WeChatNotificationSource(config);
        _bubbles = new ToastManager(config, dispatcher);
        Hotkeys = new GlobalHotkeys(config, dispatcher, _wechat);
        _notifications = new NotificationPresenter(config, _bubbles, _windows);
        _pauseTimer = dispatcher.CreateTimer();
        _pauseTimer.IsRepeating = false;
        _pauseTimer.Tick += (_, _) => SetPaused(false);
        _tray.SettingsRequested += OpenSettings;
        _tray.TogglePauseRequested += () => SetPaused(!Paused);
        _tray.TestRequested += ShowTest;
        _tray.ReloadRequested += Reload;
        _tray.ExitRequested += () => ((App)Microsoft.UI.Xaml.Application.Current).Quit();
        _bubbles.ActivateChat = _ => ActivateWeChat();
        _bubbles.SuppressNotificationsRequested += duration => SetPaused(true, duration);
        _windows.Activated += ActivateWeChat;
        Hotkeys.Failed += error => App.ShowError("微信热键失败", error);
        _source.MessageArrived += OnMessage;
        _source.SessionsUpdated += OnSessionsUpdated;
        _source.ConnectionChanged += OnConnectionChanged;
        _source.Diagnostic += OnDiagnostic;
        _wechat.Changed += OnWeChatStateChanged;
    }

    public void Start()
    {
        _notifications.ApplyMode();
        _wechat.Start();
        Hotkeys.Start();
        _source.Start();
    }

    private void OnMessage(SessionInfo session)
    {
        var detected = _wechat.Current;
        Post(() =>
        {
            try
            {
                var disposition = NotificationPolicy.Decide(Config, detected, _wechat.Current,
                    Config.SuppressWhenFullscreen && WeChat_Notifier.Infrastructure.Windows.Interop.Native.IsQuietForNotifications());
                if (disposition == NotificationDisposition.Ignore) return;
                if (disposition == NotificationDisposition.Silent) _windows.Show(session, Config, suppressDisplay: true);
                else _notifications.Show(session);
            }
            catch (Exception ex) { NotificationError("通知投递失败", ex); }
        });
    }

    private void OnWeChatStateChanged()
    {
        if (Config.SuppressForActiveChat && _wechat.Current == WeChatWindowState.Foreground)
            _bubbles.ClearAll();
        StatusChanged?.Invoke();
    }

    public string WeChatStateText => _wechat.StatusText;

    private void OnSessionsUpdated(IReadOnlyList<WeChatSession> sessions)
    {
        try { _windows.SynchronizeReadSessionsAsync(sessions).GetAwaiter().GetResult(); }
        catch (Exception ex) { Post(() => NotificationError("同步已读通知失败", ex)); }
    }

    private void NotificationError(string title, Exception error)
    {
        Log.Error(title, error);
        ConnectionText = title + "：" + error.Message;
        _tray.SetState("notification-error");
        _settings?.ReportError(title, error.Message);
        StatusChanged?.Invoke();
    }

    private void OnConnectionChanged(string state) => Post(() =>
    {
        ConnectionText = state == "connected" ? "已连接微信" : "等待微信";
        _tray.SetState(state);
        StatusChanged?.Invoke();
    });

    private void OnDiagnostic(string message)
    {
        if (message.Length == 0) return;
        Post(() => { ConnectionText = "等待微信：" + message; StatusChanged?.Invoke(); });
    }

    public void ActivateWeChat()
    {
        _ = Task.Run(() =>
        {
            try
            {
                if (!WeChatWindow.ActivateOrStart(Config)) throw new InvalidOperationException("无法唤起微信，请检查微信快捷键和启动路径。");
            }
            catch (Exception ex) { Post(() => App.ShowError("无法唤起微信", ex)); }
        });
    }

    public void ShowTest()
    {
        try { _notifications.ShowTest(); }
        catch (Exception ex) { App.ShowError("无法显示测试通知", ex); }
    }

    public void Preview(AppConfig working) => _notifications.Preview(working);
    public void PreviewAppearance(AppConfig working) => _bubbles.ShowPreview(working);
    public void EndPreview() => _bubbles.DismissPreview();

    public void OpenSettings()
    {
        if (_settings != null) { _settings.AppWindow.Show(); _settings.Activate(); return; }
        _settings = new MainWindow(this);
        _settings.Closed += (_, _) => { EndPreview(); _settings = null; };
        _settings.Activate();
    }

    public MainWindow? SettingsWindow => _settings;

    public void DisableCloseToTrayHint()
    {
        var saved = new AppConfig();
        ConfigStore.CopyInto(Config, saved);
        saved.ShowCloseToTrayHint = false;
        ConfigStore.Save(saved);
        Config.ShowCloseToTrayHint = false;
    }

    public void Save(AppConfig working)
    {
        ConfigStore.Validate(working);
        _windows.ValidateAvailable();
        var autoStartChanged = working.AutoStart != Config.AutoStart;
        if (autoStartChanged) AutoStart.Set(working.AutoStart);
        try { ConfigStore.Save(working); }
        catch (Exception saveError)
        {
            if (autoStartChanged)
            {
                try { AutoStart.Set(Config.AutoStart); }
                catch (Exception rollbackError) { throw new AggregateException("保存失败，且无法恢复开机启动设置。", saveError, rollbackError); }
            }
            throw;
        }
        ConfigStore.CopyInto(working, Config);
        ApplyConfig();
    }

    private void Reload()
    {
        try
        {
            var fresh = ConfigStore.Load();
            _windows.ValidateAvailable();
            ConfigStore.CopyInto(fresh, Config);
            ApplyConfig();
        }
        catch (Exception ex) { App.ShowError("无法重载配置", ex); }
    }

    private void ApplyConfig()
    {
        Log.Verbose = Config.VerboseLog;
        _notifications.ApplyMode();
        _bubbles.DismissPreview();
        _bubbles.Relayout();
        Hotkeys.ReRegister();
        _source.Nudge();
        StatusChanged?.Invoke();
    }

    public void SetPaused(bool paused, TimeSpan? duration = null)
    {
        _source.SetPaused(paused);
        _tray.SetPaused(paused);
        _pauseTimer.Stop();
        if (paused && duration is { } span) { _pauseTimer.Interval = span; _pauseTimer.Start(); }
        StatusChanged?.Invoke();
    }

    private void Post(Action action)
    {
        if (_disposed) return;
        if (!_dispatcher.TryEnqueue(() => { if (!_disposed) action(); }))
            Log.Warn("UI 队列已经关闭，无法投递应用事件。");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pauseTimer.Stop();
        _source.MessageArrived -= OnMessage;
        _source.SessionsUpdated -= OnSessionsUpdated;
        _source.ConnectionChanged -= OnConnectionChanged;
        _source.Diagnostic -= OnDiagnostic;
        _source.Dispose();
        _settings?.CloseForExit();
        _bubbles.Dispose();
        Hotkeys.Dispose();
        _tray.Dispose();
        _wechat.Changed -= OnWeChatStateChanged;
        _wechat.Dispose();
        _windows.Activated -= ActivateWeChat;
        _windows.Dispose();
    }
}
