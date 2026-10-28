using WeChat_Notifier.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Debugging;

namespace WeChat_Notifier.Infrastructure.Logging;

public static class Log
{
    private static readonly object Gate = new();
    private static readonly LoggingLevelSwitch Level = new(LogEventLevel.Information);
    private static Logger? _logger;
    public static bool Verbose
    {
        get => Level.MinimumLevel == LogEventLevel.Debug;
        set => Level.MinimumLevel = value ? LogEventLevel.Debug : LogEventLevel.Information;
    }
    public static string Path => AppPaths.LogFile;
    public static void Initialize()
    {
        lock (Gate)
        {
            if (_logger != null) return;
            AppPaths.Ensure();
            // 初始化失败必须显式报错；不把无日志的运行伪装成正常。
            using (File.Open(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { }
            SelfLog.Enable(Console.Error);
            _logger = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(Level)
                .WriteTo.File(Path, fileSizeLimitBytes: 2 * 1024 * 1024, rollOnFileSizeLimit: true,
                    retainedFileCountLimit: 14, shared: true,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
        }
    }

    public static void Info(string message) => Current.Information("{Message}", message);
    public static void Warn(string message) => Current.Warning("{Message}", message);
    public static void Error(string message) => Current.Error("{Message}", message);
    public static void Error(string message, Exception error) => Current.Error(error, "{Message}", message);
    public static void Debug(string message) { if (Verbose) Current.Debug("{Message}", message); }

    public static void Close()
    {
        lock (Gate) { _logger?.Dispose(); _logger = null; }
    }

    private static ILogger Current
    {
        get { Initialize(); return _logger!; }
    }
}
