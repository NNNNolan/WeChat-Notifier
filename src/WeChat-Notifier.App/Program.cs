using Microsoft.UI.Dispatching;
using WeChat_Notifier.Configuration;
using WeChat_Notifier.Diagnostics;
using WeChat_Notifier.Infrastructure.Logging;
using WeChat_Notifier.Infrastructure.WeChat.Database;
using WeChat_Notifier.Infrastructure.WeChat;
using WeChat_Notifier.Infrastructure.Windows.Interop;
using WeChat_Notifier.Infrastructure.Windows;

namespace WeChat_Notifier;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        try
        {
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture =
                System.Globalization.CultureInfo.GetCultureInfo("zh-CN");
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture =
                System.Globalization.CultureInfo.GetCultureInfo("zh-CN");
            if (!Native.SetThreadPreferredUILanguages(8, "zh-CN\0\0", out _))
                throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(),
                    "无法设置简体中文界面语言。");
            if (args.Contains("--selftest")) return SelfTest.Run();
            if (args.Contains("--probe-wechat")) return WeChatProbe.Run();
            if (args.Contains("--activate")) return WeChatWindow.ActivateOrStart(ConfigStore.Load()) ? 0 : 1;
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Microsoft.UI.Xaml.Application.Start(callback =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                _ = new App();
            });
            return Environment.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Error("启动失败", ex);
            Console.Error.WriteLine(ex);
            if (!args.Any(a => a.StartsWith("--probe-", StringComparison.Ordinal) || a == "--selftest"))
                WeChat_Notifier.Infrastructure.Windows.Interop.Native.MessageBox(0, ex.Message, "WeChat-Notifier 启动失败", 0x10);
            return 1;
        }
        finally { WeChat_Notifier.Infrastructure.Windows.Interop.GdiPlus.Shutdown(); }
    }
}
