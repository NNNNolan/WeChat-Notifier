using System.ComponentModel;
using System.Text;
using WeChat_Notifier.Infrastructure.Windows.Interop;

namespace WeChat_Notifier.Configuration;

/// <summary>便携版用户文件位于 EXE 目录；MSIX 使用包的 LocalState。</summary>
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
        if (!string.IsNullOrWhiteSpace(diagnostic)) return Path.GetFullPath(diagnostic);

        uint length = 0;
        var result = Native.GetCurrentPackageFamilyName(ref length, null);
        if (result == Native.APPMODEL_ERROR_NO_PACKAGE) return ProgramDirectory;
        if (result != Native.ERROR_INSUFFICIENT_BUFFER) throw new Win32Exception(result, "无法查询 MSIX 包身份。");

        var familyName = new StringBuilder((int)length);
        result = Native.GetCurrentPackageFamilyName(ref length, familyName);
        if (result != 0) throw new Win32Exception(result, "无法读取 MSIX 包身份。");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages", familyName.ToString(), "LocalState");
    }
}
