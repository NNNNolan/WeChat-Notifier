using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WeChat_Notifier.Application;
using WeChat_Notifier.Configuration;
using WeChat_Notifier.Diagnostics;
using WeChat_Notifier.Infrastructure.Logging;
using WeChat_Notifier.Infrastructure.Notifications;
using WeChat_Notifier.Infrastructure.Windows.Interop;
using WeChat_Notifier.Infrastructure.Windows;

namespace WeChat_Notifier;

public partial class App : Microsoft.UI.Xaml.Application
{
    private ApplicationController? _controller;
    private Mutex? _instance;
    private bool _ownsMutex;
    private DispatcherQueueTimer? _smokeTimer;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            Log.Error("未处理的界面异常", args.Exception);
            // 不把程序状态未知的异常伪装为可恢复错误。
            Native.MessageBox(0, args.Exception.Message, "WeChat-Notifier 发生错误", 0x10);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var flags = Environment.GetCommandLineArgs().Skip(1).ToArray();
        try
        {
            AppPaths.Ensure();
            var config = ConfigStore.Load();
            Log.Verbose = config.VerboseLog;
            if (flags.Contains("--system-notifications")) config.UseWindowsNotifications = true;
            if (flags.Contains("--probe-system-toast"))
            {
                using var notifications = new WindowsNotificationService();
                Quit(WindowsNotificationProbe.Run(notifications, config));
                return;
            }
            var diagnostic = flags.Contains("--smoke-test") || flags.Contains("--shot-settings") || flags.Contains("--ui-smoke");
            _instance = new Mutex(true, @"Local\WeChatNotifier.WinUI." + (diagnostic ? Environment.ProcessId : "Main"), out _ownsMutex);
            if (!_ownsMutex) { Quit(); return; }
            _controller = new ApplicationController(config, DispatcherQueue.GetForCurrentThread());
            _controller.Start();
            if (!flags.Contains("--background") || flags.Contains("--settings") || flags.Contains("--shot-settings") || flags.Contains("--ui-smoke"))
                _controller.OpenSettings();
            if (flags.Contains("--test")) _controller.ShowTest();
            if (flags.Contains("--shot-settings") || flags.Contains("--ui-smoke"))
            {
                var index = Array.IndexOf(flags, "--shot-settings");
                var directory = index >= 0 && index + 1 < flags.Length ? flags[index + 1] : Path.Combine(AppPaths.LogDirectory, "screenshots");
                _ = RunUiChecksAsync(directory, flags.Contains("--ui-smoke"));
            }
            if (flags.Contains("--smoke-test"))
            {
                _controller.Preview(config);
                _smokeTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
                _smokeTimer.IsRepeating = false;
                _smokeTimer.Interval = TimeSpan.FromSeconds(6);
                _smokeTimer.Tick += (_, _) => Quit();
                _smokeTimer.Start();
            }
        }
        catch (Exception ex) { ShowError("启动失败", ex); Quit(1); }
    }

    private async Task RunUiChecksAsync(string directory, bool verify)
    {
        try
        {
            await _controller!.SettingsWindow!.CapturePagesAsync(directory, verify);
            Quit();
        }
        catch (Exception ex) { Log.Error("界面验证失败", ex); Quit(1); }
    }

    internal static void ShowError(string title, Exception error)
    {
        Log.Error(title, error);
        if (Current is App app && app._controller?.SettingsWindow is { } window)
            window.ReportError(title, error.Message);
        else Native.MessageBox(0, error.Message, title, 0x10);
    }

    public void Quit(int exitCode = 0)
    {
        _smokeTimer?.Stop();
        try { _controller?.Dispose(); }
        catch (Exception ex) { Log.Error("释放应用服务失败", ex); exitCode = 1; }
        _controller = null;
        if (_ownsMutex) { _instance?.ReleaseMutex(); _ownsMutex = false; }
        _instance?.Dispose();
        _instance = null;
        Environment.ExitCode = exitCode;
        Exit();
    }
}
