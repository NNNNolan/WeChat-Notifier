namespace WeChat_Notifier.Configuration;

/// <summary>用户文件位于真实 EXE 目录；单文件解包目录只用于加载程序资源。</summary>
public static class AppPaths
{
    public static string ProgramDirectory { get; } = DirectoryForExecutable(
        Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序运行目录。"));
    public static string RootDirectory { get; } = ResolveRoot();
    public static string ConfigDirectory => Path.Combine(RootDirectory, "Config");
    public static string LogDirectory => Path.Combine(RootDirectory, "Log");
    public static string ConfigFile => Path.Combine(ConfigDirectory, "config.json");
    public static string LogFile => Path.Combine(LogDirectory, "app.log");

    public static void Ensure()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    internal static string DirectoryForExecutable(string executable) =>
        Path.GetDirectoryName(Path.GetFullPath(executable))
        ?? throw new InvalidOperationException("程序运行路径没有父目录。");

    private static string ResolveRoot()
    {
        // 回归测试隔离文件，不改动实际运行配置。
        var diagnostic = Environment.GetEnvironmentVariable("WECHAT_NOTIFIER_DATA_DIR");
        return string.IsNullOrWhiteSpace(diagnostic) ? ProgramDirectory : Path.GetFullPath(diagnostic);
    }
}
