using WeChat_Notifier.Application;
using WeChat_Notifier.Application.Notifications;
using WeChat_Notifier.Configuration;
using WeChat_Notifier.Domain;
using WeChat_Notifier.Infrastructure.Logging;
using WeChat_Notifier.Infrastructure.Notifications;
using WeChat_Notifier.Infrastructure.Windows;
using WeChat_Notifier.Infrastructure.Windows.Interop;
using WeChat_Notifier.ViewModels;

namespace WeChat_Notifier.Diagnostics;

internal static class ModeChecks
{
    internal static string? Rules()
    {
        var config = new AppConfig
        {
            NotificationMode = "Privacy",
            SilentApplications = [@"C:\Programs\Game.exe"],
            PrivacyApplications = [@"C:\Programs\Meeting.exe"]
        };
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Require(ApplicationModeController.Resolve(config, running) == new ModeState(NotificationMode.Privacy, null),
            "没有应用运行时未恢复手动模式");
        running.Add(@"C:\Programs\Meeting.exe");
        Require(ApplicationModeController.Resolve(config, running).Mode == NotificationMode.Privacy, "隐私应用未激活模式");
        running.Add(@"C:\Programs\Game.exe");
        Require(ApplicationModeController.Resolve(config, running).Mode == NotificationMode.Silent, "静默没有优先于隐私");
        running.Remove(@"C:\Programs\Game.exe");
        Require(ApplicationModeController.Resolve(config, running).Mode == NotificationMode.Privacy, "静默应用退出未恢复隐私");
        config.NotificationMode = "Normal";
        running.Clear();
        Require(ApplicationModeController.Resolve(config, running).Mode == NotificationMode.Normal, "自动模式改变了手动模式");
        var view = new SettingsViewModel(config);
        Require(view.ContainsInOtherList(@"c:\programs\sub\..\meeting.EXE", true), "等价路径没有识别为跨名单重复");
        view.AddApplication(@"C:\Programs\Meeting.exe", true);
        Require(view.SilentApplications.Count == 2 && view.PrivacyApplications.Count == 0,
            "确认移动后旧名单未删除或新名单丢失");
        view.AddApplication(@"c:\programs\MEETING.exe", true);
        Require(view.SilentApplications.Count == 2, "同名单允许了重复路径");
        Require(config.PrivacyApplications.Count == 1 && config.SilentApplications.Count == 1, "未保存编辑污染运行配置");
        var bad = new AppConfig { SilentApplications = [@"C:\Programs\Meeting.exe"], PrivacyApplications = [@"C:\Programs\.\MEETING.exe"] };
        try { ConfigStore.Validate(bad); return "外部配置可绕过重复路径规则"; }
        catch (InvalidDataException) { }
        bad = new AppConfig { ModeSwitchHotkey = "Ctrl+Alt+W" };
        try { ConfigStore.Validate(bad); return "两个快捷键可以配置成同一组合"; }
        catch (InvalidDataException) { }
        bad = new AppConfig { ModeSwitchHotkey = "Alt+Ctrl+W" };
        try { ConfigStore.Validate(bad); return "调整修饰键顺序绕过了快捷键冲突检查"; }
        catch (InvalidDataException) { }
        Require(new AppConfig().ModeSwitchHotkey == "Ctrl+Alt+,+.", "模式热键默认值错误");
        Require(GlobalHotkeys.SameHotkey("Ctrl+Alt+,+.", "Alt+Ctrl+.+,"), "双主键顺序没有规范化");
        Require(!GlobalHotkeys.SameHotkey("Ctrl+Alt+,+.", "Ctrl+Alt+,"), "双主键与单主键被误认为相同");
        bad = new AppConfig { WeChatHotkey = "Ctrl+Alt+,+." };
        try { ConfigStore.Validate(bad); return "微信热键接受了不能发送的双主键"; }
        catch (InvalidDataException) { }
        return null;
    }

    internal static string? Privacy()
    {
        var detail = new SessionInfo
        {
            Key = "private-chat", Title = "机密客户", Preview = "不能出现在横幅中的内容",
            Unread = 2, NewMessageCount = 2, Time = "12:34"
        };
        foreach (var mode in Enum.GetValues<NotificationMode>())
        foreach (var system in new[] { false, true })
        {
            var config = new AppConfig { UseWindowsNotifications = system, PlaySound = true };
            var plan = NotificationPresentation.Create(detail, config, mode);
            Require(plan.Center.Title == detail.Title && plan.Center.Preview == detail.Preview, "通知中心丢失详细信息");
            if (mode == NotificationMode.Silent)
                Require(plan.Popup == null && !plan.BubbleSound, "静默模式仍有弹窗或声音");
            if (mode == NotificationMode.Privacy)
                Require(plan.Popup?.Title == "微信新消息" && plan.Popup.Preview == "收到了2条消息"
                    && plan.WindowsPopup == system, "隐私弹窗泄露详情或错用了通知渠道");
            if (mode == NotificationMode.Normal)
                Require(plan.WindowsPopup == system && plan.Popup == detail, "正常模式渠道错误");
            var quiet = NotificationPresentation.Create(detail, config, mode, true);
            Require(quiet.Popup == null && !quiet.BubbleSound, "全屏抑制被模式绕过");
        }
        var original = WindowsNotificationService.CreateNotification(detail, new AppConfig { PlaySound = true });
        var retained = WindowsNotificationService.WithoutPopup(original);
        Require(retained.SuppressDisplay && retained.Tag == original.Tag && retained.Group == original.Group
            && retained.Expiration == original.Expiration && retained.Payload.Contains(detail.Preview)
            && retained.Payload.Contains("silent=\"true\""), "旧横幅撤回后未静音保留通知中心详情");
        return null;
    }

    internal static string? Badges()
    {
        var unread = new UnreadNotifications();
        var snapshot = new WeChatSession("a", "A", "", 0, "", 10, false, false);
        unread.Synchronize([snapshot]);
        Require(unread.Count == 0, "启动历史未读进入新角标");
        unread.Arrived(new SessionInfo { Key = "a", Unread = 11 });
        unread.Synchronize([snapshot with { Unread = 11 }]);
        Require(unread.Count == 1, "新消息角标未保持");
        unread.Arrived(new SessionInfo { Key = "a", Unread = 14, NewMessageCount = 3 });
        unread.Synchronize([snapshot with { Unread = 14 }]);
        Require(unread.Count == 4, "同次快照内的多条消息被少计");
        unread.Synchronize([snapshot with { Unread = 12 }]);
        Require(unread.Count == 2, "部分已读后角标未按减少数更新");
        unread.Synchronize([snapshot with { Unread = 0 }]);
        Require(unread.Count == 0, "已读后角标未清除");
        foreach (var mode in Enum.GetValues<NotificationMode>())
        {
            var pixels = IconLoader.RenderTrayPixels(mode);
            if (mode != NotificationMode.Normal)
            {
                var expected = mode == NotificationMode.Privacy ? (r: 0x4E, g: 0x6E, b: 0xF2) : (r: 0x28, g: 0x28, b: 0x2B);
                var found = false;
                for (var i = 0; i < pixels.Length; i += 4)
                    if (pixels[i + 3] == 255 && pixels[i] == expected.b && pixels[i + 1] == expected.g && pixels[i + 2] == expected.r)
                        found = true;
                Require(found, "模式图标色值错误：" + mode);
            }
            var icon = IconLoader.CreateTrayIcon(mode, 9);
            Require(icon != 0 && Native.DestroyIcon(icon), "动态图标资源无法创建/释放");
        }
        return null;
    }

    internal static string? Serilog()
    {
        var marker = "serilog-check-" + Guid.NewGuid().ToString("N");
        var verbose = Log.Verbose;
        Log.Verbose = false;
        Log.Debug(marker + "-hidden");
        Log.Info(marker);
        Log.Error("异常保留测试", new InvalidOperationException(marker + "-exception"));
        using var file = new FileStream(AppPaths.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file);
        var text = reader.ReadToEnd();
        Require(text.Contains(marker + "-exception") && text.Contains("InvalidOperationException") && text.Contains(marker),
            "Serilog 文件日志或异常丢失");
        Require(!text.Contains(marker + "-hidden"), "关闭详细日志后仍输出 Debug");
        Log.Verbose = verbose;
        return null;
    }

    internal static string? ProcessPaths()
    {
        var own = Environment.ProcessPath!;
        Require(RunningApplicationPaths.Find([own]).Contains(own), "运行进程完整路径识别失败");
        var other = Path.Combine(Path.GetDirectoryName(own)!, "other", Path.GetFileName(own));
        Require(!RunningApplicationPaths.Find([other]).Contains(other), "同名不同路径误触发模式");
        return null;
    }

    private static void Require(bool condition, string error)
    {
        if (!condition) throw new InvalidOperationException(error);
    }
}
