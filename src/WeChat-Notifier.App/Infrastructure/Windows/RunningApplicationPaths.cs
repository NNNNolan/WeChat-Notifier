using System.ComponentModel;
using System.Diagnostics;
using WeChat_Notifier.Configuration;
using WeChat_Notifier.Infrastructure.Logging;
using WeChat_Notifier.Infrastructure.Windows.Interop;

namespace WeChat_Notifier.Infrastructure.Windows;

internal static class RunningApplicationPaths
{
    internal static IReadOnlySet<string> Find(IEnumerable<string> configured)
    {
        var paths = configured.Select(ApplicationRules.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in paths.Select(Path.GetFileNameWithoutExtension).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var processes = Process.GetProcessesByName(name!);
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        var path = Native.ProcessPathOf((uint)process.Id);
                        if (path != null && paths.Contains(path)) found.Add(path);
                    }
                    catch (InvalidOperationException) { /* 进程在枚举与查询之间退出。 */ }
                    catch (Win32Exception ex) { Log.Debug($"无法读取模式应用 {process.Id}：{ex.Message}"); }
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        return found;
    }
}
