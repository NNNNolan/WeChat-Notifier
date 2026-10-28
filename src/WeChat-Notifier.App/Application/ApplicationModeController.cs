using Microsoft.UI.Dispatching;
using WeChat_Notifier.Configuration;
using WeChat_Notifier.Domain;
using WeChat_Notifier.Infrastructure.Logging;
using WeChat_Notifier.Infrastructure.Windows;

namespace WeChat_Notifier.Application;

internal enum ModeChangeReason { Manual, ApplicationStarted, ApplicationExited, Settings }

internal sealed record ModeState(NotificationMode Mode, string? TriggerPath)
{
    internal bool Automatic => TriggerPath != null;
}

internal sealed record NotificationModeChanged(ModeState Previous, ModeState Current, ModeChangeReason Reason);

/// <summary>维护手动模式和应用程序自动覆盖；静默覆盖优先于隐私。</summary>
internal sealed class ApplicationModeController : IDisposable
{
    private readonly AppConfig _config;
    private readonly DispatcherQueueTimer _timer;
    private bool _disposed;

    internal event Action<NotificationModeChanged>? Changed;
    internal ModeState State { get; private set; }
    internal NotificationMode Current => State.Mode;

    internal ApplicationModeController(AppConfig config, DispatcherQueue dispatcher)
    {
        _config = config;
        State = new(NotificationModeParser.Parse(config.NotificationMode), null);
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += OnTick;
    }

    internal void Start()
    {
        Refresh();
        _timer.Start();
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        try { Refresh(); }
        catch (Exception ex) { Log.Error("自动通知模式检测失败", ex); App.ShowError("无法检测模式应用", ex); }
    }

    internal void Refresh(ModeChangeReason reason = ModeChangeReason.Settings)
    {
        if (_disposed) return;
        ApplyRunning(RunningApplicationPaths.Find(_config.SilentApplications.Concat(_config.PrivacyApplications)), reason);
    }

    internal void ApplyRunning(IReadOnlySet<string> running, ModeChangeReason reason = ModeChangeReason.Settings)
    {
        var next = Resolve(_config, running);
        if (next == State) return;
        var previous = State;
        State = next;
        if (reason != ModeChangeReason.Manual)
            reason = next.Automatic ? ModeChangeReason.ApplicationStarted
                : previous.Automatic ? ModeChangeReason.ApplicationExited : ModeChangeReason.Settings;
        Changed?.Invoke(new(previous, next, reason));
    }

    internal static ModeState Resolve(AppConfig config, IReadOnlySet<string> running)
    {
        foreach (var path in config.SilentApplications)
            if (running.Contains(ApplicationRules.Normalize(path))) return new(NotificationMode.Silent, path);
        foreach (var path in config.PrivacyApplications)
            if (running.Contains(ApplicationRules.Normalize(path))) return new(NotificationMode.Privacy, path);
        return new(NotificationModeParser.Parse(config.NotificationMode), null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}
