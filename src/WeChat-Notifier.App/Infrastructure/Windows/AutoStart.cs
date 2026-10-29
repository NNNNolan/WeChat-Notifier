using Microsoft.Win32;
using System.Diagnostics;
using WeChat_Notifier.Infrastructure.Logging;

namespace WeChat_Notifier.Infrastructure.Windows;

public static class AutoStart
{
    private const string LegacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyValueName = "WeChat-Notifier";
    private const string ShortcutName = "WeChat-Notifier.lnk";

    public static void Migrate(bool enabled)
    {
        if (!HasOwnLegacyRegistration()) return;

        Set(enabled);
        RemoveOwnLegacyRegistration();
    }

    public static void Set(bool enabled)
    {
        if (enabled) CreateStartupShortcut();
        else RemoveStartupShortcut();

        Log.Info($"autostart {(enabled ? "enabled" : "disabled")}");
    }

    private static string StartupShortcutPath
    {
        get
        {
            var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            if (string.IsNullOrWhiteSpace(startup)) throw new InvalidOperationException("无法确定当前用户的启动文件夹。");
            return Path.Combine(startup, ShortcutName);
        }
    }

    private static void CreateStartupShortcut()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定本程序的路径。");
        var workingDirectory = Path.GetDirectoryName(executable)
            ?? throw new InvalidOperationException("程序运行路径没有父目录。");
        var shortcut = StartupShortcutPath;
        var script = "$ErrorActionPreference = 'Stop'; "
            + "$shell = New-Object -ComObject WScript.Shell; "
            + "$link = $shell.CreateShortcut(" + PowerShellLiteral(shortcut) + "); "
            + "$link.TargetPath = " + PowerShellLiteral(executable) + "; "
            + "$link.Arguments = '--background'; "
            + "$link.WorkingDirectory = " + PowerShellLiteral(workingDirectory) + "; "
            + "$link.Save()";
        RunPowerShell(script);
    }

    private static void RemoveStartupShortcut()
    {
        var shortcut = StartupShortcutPath;
        if (!File.Exists(shortcut)) return;

        var script = "$ErrorActionPreference = 'Stop'; Remove-Item -LiteralPath "
            + PowerShellLiteral(shortcut) + " -Force";
        RunPowerShell(script);
    }

    private static void RunPowerShell(string script)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("找不到 Windows PowerShell。", executable);

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using var process = Process.Start(startInfo)
            ?? throw new IOException("无法启动 Windows PowerShell 以更新启动快捷方式。");
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new IOException("无法更新开机启动快捷方式：" + error.GetAwaiter().GetResult().Trim());
    }

    private static string PowerShellLiteral(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static bool HasOwnLegacyRegistration()
    {
        using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKey);
        return key?.GetValue(LegacyValueName) is string commandLine
            && IsOwnLegacyCommand(commandLine);
    }

    private static void RemoveOwnLegacyRegistration()
    {
        using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKey, writable: true);
        if (key?.GetValue(LegacyValueName) is string commandLine && IsOwnLegacyCommand(commandLine))
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
    }

    private static bool IsOwnLegacyCommand(string commandLine)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(commandLine)) return false;

        var value = commandLine.TrimStart();
        if (value.StartsWith('"'))
        {
            var endQuote = value.IndexOf('"', 1);
            return endQuote > 1
                && string.Equals(value[1..endQuote], executable, StringComparison.OrdinalIgnoreCase);
        }

        return value.StartsWith(executable, StringComparison.OrdinalIgnoreCase)
            && (value.Length == executable.Length || char.IsWhiteSpace(value[executable.Length]));
    }
}
