using System.Text;
using WeChat_Notifier.Configuration;

namespace WeChat_Notifier.Infrastructure.Logging;

public static class Log
{
    private static readonly object Gate = new();
    public static bool Verbose { get; set; }
    public static string Path => AppPaths.LogFile;
    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);
    public static void Error(string message, Exception error) => Write("ERROR", $"{message}: {error}");
    public static void Debug(string message) { if (Verbose) Write("DEBUG", message); }

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        try
        {
            lock (Gate)
            {
                AppPaths.Ensure();
                if (File.Exists(Path) && new FileInfo(Path).Length > 2 * 1024 * 1024)
                    File.Move(Path, Path + ".1", overwrite: true);
                File.AppendAllText(Path, line, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.Write(line);
            Console.Error.WriteLine("日志写入失败：" + ex.Message);
        }
    }
}
