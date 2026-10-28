using Microsoft.Win32;
using WeChat_Notifier.Infrastructure.Logging;

namespace WeChat_Notifier.Infrastructure.Windows;

public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WeChat-Notifier";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string { Length: > 0 };
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new IOException("无法打开当前用户的开机启动注册表项。");
        if (enabled)
        {
            var path = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定本程序的路径。");
            key.SetValue(ValueName, $"\"{path}\" --background");
        }
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
        Log.Info($"autostart {(enabled ? "enabled" : "disabled")}");
    }
}
