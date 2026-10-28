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
    private readonly ApplicationModeController _mode;
    private readonly WeChatNotificationSource _source;
    private readonly ToastManager _bubbles;
    private readonly WindowsNotificationService _windows = new();
    private readonly NotificationPresenter _notifications;
    private readonly TrayIcon _tray;
    private readonly UnreadNotifications _unread = new();
    private readonly DispatcherQueueTimer _pauseTimer;
    private MainWindow? _settings;
    private WeChatWindowState _lastWeChatState;
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
        _tray = new TrayIcon(dispatcher);
        _wechat = new WeChatStateMonitor(dispatcher);
        _mode = new ApplicationModeController(config, dispatcher);
        _source = new WeChatNotificationSource(config, () => _wechat.IsRunning);
        _bubbles = new ToastManager(config, dispatcher);
        Hotkeys = new GlobalHotkeys(config, dispatcher, _wechat);
        Hotkeys.ModeSwitchRequested += ToggleManualMode;
        _notifications = new NotificationPresenter(config, _bubbles, _windows);
        _pauseTimer = dispatcher.CreateTimer();
        _pauseTimer.IsRepeating = false;
        _pauseTimer.Tick += (_, _) => SetPaused(false);
        _tray.SettingsRequested += OpenSettings;
        _tray.WeChatRequested += ActivateWeChat;
        _tray.ModeRequested += SetManualMode;
        _tray.TogglePauseRequested += () => SetPaused(!Paused);
        _tray.TestRequested += ShowTest;
        _tray.ReloadRequested += Reload;
        _tray.ExitRequested += () => ((App)Microsoft.UI.Xaml.Application.Current).Quit();
        _tray.SystemResumed += _source.RefreshConnection;
        _bubbles.ActivateChat = _ => ActivateWeChat();
        _bubbles.SuppressNotificationsRequested += duration => SetPaused(true, duration);
        _windows.Activated += ActivateWeChat;
        Hotkeys.Failed += error => App.ShowError("全局快捷键失败", error);
        _source.MessageArrived += OnMessage;
        _source.SessionsUpdated += OnSessionsUpdated;
        _source.ConnectionChanged += OnConnectionChanged;
        _source.Diagnostic += OnDiagnostic;
        _wechat.Changed += OnWeChatStateChanged;
        _mode.Changed += OnModeChanged;
    }

    public void Start(bool listenForMessages = true)
    {
        _mode.Start();
        _tray.SetMode(_mode.Current);
        _notifications.ApplyMode(_mode.Current);
        _wechat.Start();
        Hotkeys.Start();
        if (listenForMessages) _source.Start();
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
                _unread.Arrived(session);
                _tray.SetUnreadCount(_unread.Count);
                if (_mode.Current != NotificationMode.Silent) _tray.Flash();
                _notifications.Show(session, _mode.Current, suppressDisplay: disposition == NotificationDisposition.Silent);
            }
            catch (Exception ex) { NotificationError("通知投递失败", ex); }
        });
    }

    private void OnModeChanged(NotificationModeChanged change)
    {
        _tray.SetMode(change.Current.Mode);
        _notifications.ApplyMode(change.Current.Mode);
        if (change.Reason != ModeChangeReason.Manual
            && (change.Reason == ModeChangeReason.Settings || Config.NotifyOnAutomaticModeChange))
        {
            try
            {
                var body = change.Reason == ModeChangeReason.Settings ? $"当前为{change.Current.Mode.DisplayName()}。"
                    : change.Current.Automatic
                    ? $"检测到{Path.GetFileName(change.Current.TriggerPath)}启动，进入{change.Current.Mode.DisplayName()}。"
                    : $"模式应用已退出，恢复{change.Current.Mode.DisplayName()}。";
                _bubbles.ShowStatus("通知模式已切换", body);
            }
            catch (Exception ex) { Log.Error("模式切换通知失败", ex); }
        }
        StatusChanged?.Invoke();
    }

    private void ToggleManualMode() => SetManualMode(NotificationModeParser.Parse(Config.NotificationMode).Next());

    private void SetManualMode(NotificationMode next)
    {
        try
        {
            var saved = new AppConfig();
            ConfigStore.CopyInto(Config, saved);
            saved.NotificationMode = next.ToString();
            ConfigStore.Save(saved);
            Config.NotificationMode = saved.NotificationMode;
            _settings?.SynchronizeManualMode(saved.NotificationMode);
            _mode.Refresh(ModeChangeReason.Manual);
            var body = _mode.State.Automatic
                ? $"手动选择{next.DisplayName()}；{Path.GetFileName(_mode.State.TriggerPath)}运行中，当前保持{_mode.Current.DisplayName()}。"
                : $"当前为{_mode.Current.DisplayName()}。";
            _bubbles.ShowStatus("通知模式已切换", body);
        }
        catch (Exception ex) { App.ShowError("无法切换通知模式", ex); }
    }

    internal ApplicationModeController ModeController => _mode;
    internal ToastManager Bubbles => _bubbles;
    internal WindowsNotificationService WindowsNotifications => _windows;
    internal TrayIcon Tray => _tray;
    internal NotificationPresenter Presenter => _notifications;

    public string ModeText => _mode.Current.DisplayName() + (_mode.State.Automatic
        ? $"（自动：{Path.GetFileName(_mode.State.TriggerPath)}）" : "");

    private void OnWeChatStateChanged()
    {
        var state = _wechat.Current;
        if (state == WeChatWindowState.NotRunning || _lastWeChatState == WeChatWindowState.NotRunning)
            _source.RefreshConnection();
        else if (Config.SuppressForActiveChat && state != WeChatWindowState.Foreground
            && _lastWeChatState == WeChatWindowState.Foreground)
            _source.Nudge();
        _lastWeChatState = state;
        if (Config.SuppressForActiveChat && _wechat.Current == WeChatWindowState.Foreground)
            _bubbles.ClearAll();
        StatusChanged?.Invoke();
    }

    public string WeChatStateText => _wechat.StatusText;

    private void OnSessionsUpdated(IReadOnlyList<WeChatSession> sessions)
    {
        Post(() => { _unread.Synchronize(sessions); _tray.SetUnreadCount(_unread.Count); });
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
        Post(() =>
        {
            ConnectionText = message.Length == 0 ? "已连接微信" : "微信监听：" + message;
            StatusChanged?.Invoke();
        });
    }

    public void ActivateWeChat()
    {
        Post(() => { _unread.Acknowledge(); _tray.SetUnreadCount(0); });
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
        try { _notifications.Show(NotificationPresenter.TestSession(), _mode.Current); }
        catch (Exception ex) { App.ShowError("无法显示测试通知", ex); }
    }

    public void Preview(AppConfig working) => _notifications.Preview(working, PreviewMode(working));
    private NotificationMode PreviewMode(AppConfig working) => _mode.State.Automatic
        ? _mode.Current : NotificationModeParser.Parse(working.NotificationMode);
    public void PreviewAppearance(AppConfig working)
    {
        var mode = PreviewMode(working);
        if (mode == NotificationMode.Silent) { EndPreview(); return; }
        var popup = NotificationPresentation.Create(NotificationPresenter.TestSession(), working, mode).Popup!;
        _bubbles.ShowPreview(working, popup);
    }
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
        Hotkeys.ValidateModeHotkey(working);
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
        _mode.Refresh();
        _notifications.ApplyMode(_mode.Current);
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
        _tray.WeChatRequested -= ActivateWeChat;
        _tray.Dispose();
        _wechat.Changed -= OnWeChatStateChanged;
        _mode.Changed -= OnModeChanged;
        Hotkeys.ModeSwitchRequested -= ToggleManualMode;
        _tray.ModeRequested -= SetManualMode;
        _mode.Dispose();
        _wechat.Dispose();
        _windows.Activated -= ActivateWeChat;
        _windows.Dispose();
    }
}
