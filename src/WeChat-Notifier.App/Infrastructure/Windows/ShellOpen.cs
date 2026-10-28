using System.Diagnostics;

namespace WeChat_Notifier.Infrastructure.Windows;

public static class ShellOpen
{
    public static void File(string path)
    {
        using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        if (process == null) throw new IOException("Windows 未能打开：" + path);
    }

    public static void Folder(string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("目录不存在：" + path);
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var startInfo = new ProcessStartInfo(explorer) { UseShellExecute = false };
        startInfo.ArgumentList.Add(Path.GetFullPath(path));
        using var process = Process.Start(startInfo);
        if (process is null) throw new IOException("Windows 资源管理器未能打开：" + path);
    }
}
