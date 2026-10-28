using System.Diagnostics;

namespace WeChat_Notifier.Infrastructure.Windows;

public static class ShellOpen
{
    public static void File(string path)
    {
        using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        if (process == null) throw new IOException("Windows 未能打开：" + path);
    }

    public static void Folder(string path) => File(path);
}
