using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using WeChat_Notifier.Application.Notifications;
using WeChat_Notifier.Configuration;
using WeChat_Notifier.Domain;
using WeChat_Notifier.Infrastructure.Notifications;
using WeChat_Notifier.Infrastructure.WeChat.Database;
using WeChat_Notifier.Infrastructure.WeChat;
using WeChat_Notifier.Infrastructure.Windows.Interop;
using WeChat_Notifier.Infrastructure.Windows;
using WeChat_Notifier.ViewModels;
using WeChat_Notifier.Views.Notifications;

namespace WeChat_Notifier.Diagnostics;

internal static class SelfTest
{
    public static int Run()
    {
        var failures = 0;
        void Check(string name, Func<string?> test)
        {
            try
            {
                var error = test();
                if (error != null) throw new InvalidOperationException(error);
                Console.WriteLine("PASS " + name);
            }
            catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + ex); }
        }
        Check("SQLCipher / WAL 已提交事务", WeChatChecks.WalTransactions);
        Check("WAL/SHM 提交状态、未提交事务和原位覆盖", DatabaseMonitoringChecks.CommitState);
        Check("无变更零查询与联系人/企业缓存更新", DatabaseMonitoringChecks.QueriesAndMetadata);
        Check("真实文件事件、重命名和缓冲区溢出", DatabaseMonitoringChecks.FileEvents);
        Check("事件读取、暂停恢复与退出生命周期", DatabaseMonitoringChecks.SourceLifecycle);
        Check("企业群/联系人名称和免打扰", WeChatChecks.BusinessSessionMetadata);
        Check("消息过滤和无历史重放", WeChatChecks.NotificationPolicy);
        Check("只读内存扫描及跨块匹配", WeChatChecks.MemoryScan);
        Check("AOT 配置序列化与工作副本隔离", Settings);
        Check("自动模式优先级、退出恢复与名单移动", ModeChecks.Rules);
        Check("正常/隐私/静默通知内容边界", ModeChecks.Privacy);
        Check("模式托盘色值、角标与已读清零", ModeChecks.Badges);
        Check("Serilog 写入、异常和日志级别", ModeChecks.Serilog);
        Check("模式应用完整路径与同名程序隔离", ModeChecks.ProcessPaths);
        Check("微信状态与前台通知抑制", WindowNotificationPolicy);
        Check("EXE 目录配置和日志路径", PortablePaths);
        Check("通知 XML 编码、声音与已读撤回", SystemPayload);
        Check("热键解析与原生窗口注册", Hotkeys);
        Check("通知图标透明边缘和独立托盘图标", Icons);
        Check("液态玻璃透明度和气泡绘制", Glass);
        Console.WriteLine($"selftest: failures={failures} dynamicCode={RuntimeFeature.IsDynamicCodeSupported}");
        return failures == 0 ? 0 : 1;
    }

    private static string? Settings()
    {
        var source = new AppConfig { SkipSessions = ["好友"], WeChatPath = @"C:\Program Files\Weixin\Weixin.exe",
            ShowCloseToTrayHint = true, SuppressForActiveChat = false };
        var first = new SettingsViewModel(source);
        var second = new SettingsViewModel(source);
        first.Model.SkipSessions.Add("仅第一份副本");
        first.UseWindowsNotifications = true;
        if (source.SkipSessions.Count != 1 || second.Model.SkipSessions.Count != 1 || source.UseWindowsNotifications)
            return "工作副本泄露到正式配置";
        if (!first.IsDirty || first.UseCustomBubble) return "通知模式未更新";
        first.Revert();
        if (first.IsDirty || !first.UseCustomBubble) return "取消修改未还原";
        first.Width = source.Width + 2;
        first.SynchronizeCloseToTrayHint(false);
        if (!first.IsDirty || first.Model.ShowCloseToTrayHint) return "关闭提示偏好丢失或吞掉未保存的修改";
        first.Revert();
        if (first.IsDirty || first.Model.ShowCloseToTrayHint) return "还原工作副本重新开启了不再提示";
        first.Width = source.Width + 2;
        first.AcceptChanges();
        if (first.IsDirty) return "保存后仍标记为未保存";
        var json = ConfigStore.Serialize(source);
        var read = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppConfig)!;
        var copy = new AppConfig();
        ConfigStore.CopyInto(source, copy);
        if (ConfigStore.Serialize(copy) != json || ConfigStore.Serialize(read) != json) return "复制或序列化遗漏配置字段";
        ConfigStore.Validate(read);
        read.Width = double.NaN;
        try { ConfigStore.Validate(read); return "无效数值被接受"; }
        catch (InvalidDataException) { }
        var map = new Dictionary<string, string> { ["session\\session.db"] = "test-value" };
        if (!JsonSerializer.Serialize(map, AppJsonContext.Default.DictionaryStringString).Contains("test-value", StringComparison.Ordinal))
            return "密钥缓存字典没有生成序列化代码";
        return null;
    }

    private static string? SystemPayload()
    {
        var session = new SessionInfo { Key = "<&群", Title = "标题 <&> \"", Preview = "正文 <text> & 引用" };
        var config = new AppConfig { PlaySound = true };
        var xml = XElement.Parse(WindowsNotificationService.BuildPayload(session, config, suppressDisplay: true));
        if (!xml.Descendants("text").Select(t => t.Value).SequenceEqual([session.Title, session.Preview]))
            return "消息没有安全编码";
        if ((string?)xml.Element("audio")?.Attribute("silent") != "true") return "气泡模式记录未静音";
        xml = XElement.Parse(WindowsNotificationService.BuildPayload(session, config));
        if (xml.Element("audio") != null) return "有声系统通知被静音";
        var read = new WeChatSession("read", "", "", 0, "", 0, false, false);
        var partial = read with { Username = "partial", Unread = 2 };
        var tags = WindowsNotificationService.ReadTagsForSessions([read, partial]);
        var removed = WindowsNotificationService.FindReadNotificationTags([
            (WindowsNotificationService.TagFor("read"), "wechat"),
            (WindowsNotificationService.TagFor("partial"), "wechat"),
            (WindowsNotificationService.TagFor("read"), "another-app")], tags);
        return removed.Count == 1 && removed[0] == WindowsNotificationService.TagFor("read") ? null : "已读撤回选择错误";
    }

    private static string? WindowNotificationPolicy()
    {
        var config = new AppConfig();
        foreach (var state in Enum.GetValues<WeChatWindowState>())
        {
            var expected = state is WeChatWindowState.NotRunning or WeChatWindowState.Foreground
                ? NotificationDisposition.Ignore : NotificationDisposition.Show;
            if (NotificationPolicy.Decide(config, state, state, false) != expected) return "状态抑制错误：" + state;
        }
        if (NotificationPolicy.Decide(config, WeChatWindowState.Background, WeChatWindowState.Foreground, false)
            != NotificationDisposition.Ignore) return "队列等待期间微信进入前台仍弹出";
        if (NotificationPolicy.Decide(config, WeChatWindowState.Foreground, WeChatWindowState.Background, false)
            != NotificationDisposition.Ignore) return "离开微信前台后补弹";
        if (NotificationPolicy.Decide(config, WeChatWindowState.Hidden, WeChatWindowState.Hidden, true)
            != NotificationDisposition.Silent) return "全屏通知没有静音抑制";
        config.SuppressForActiveChat = false;
        if (NotificationPolicy.Decide(config, WeChatWindowState.Foreground, WeChatWindowState.Foreground, false)
            != NotificationDisposition.Show) return "关闭前台抑制后仍不通知";
        if (WeChatStateMonitor.Resolve(true, true, false, false, false) != WeChatWindowState.Hidden
            || WeChatStateMonitor.Resolve(true, true, true, true, false) != WeChatWindowState.Minimized
            || WeChatStateMonitor.Resolve(true, false, false, false, true) != WeChatWindowState.Foreground
            || WeChatStateMonitor.Resolve(false, false, false, false, false) != WeChatWindowState.NotRunning)
            return "微信主窗口、独立窗口或进程状态判断错误";
        return WeChatWindow.IsCandidateWindowClass("Qt51514QWindowIcon")
            && WeChatWindow.IsCandidateWindowClass("Qt6QWindowIcon")
            && !WeChatWindow.IsCandidateWindowClass("Qt51514QWindowPopup") ? null : "微信窗口识别依赖固定 Qt 版本";
    }

    private static string? PortablePaths()
    {
        var exe = Path.Combine(Path.GetTempPath(), "WeChat-Notifier-portable", "WeChat-Notifier.exe");
        if (AppPaths.DirectoryForExecutable(exe) != Path.GetDirectoryName(exe)) return "程序目录解析错误";
        if (AppPaths.ConfigFile != Path.Combine(AppPaths.RootDirectory, "Config", "config.json")
            || AppPaths.LogFile != Path.Combine(AppPaths.RootDirectory, "Log", "app.log"))
            return "配置或日志没有分目录";
        if (AppPaths.ProgramDirectory != Path.GetDirectoryName(Environment.ProcessPath)) return "单文件路径落入解包目录";
        AppPaths.Ensure();
        return Directory.Exists(AppPaths.ConfigDirectory) && Directory.Exists(AppPaths.LogDirectory)
            ? null : "配置或日志目录未建立";
    }

    private static string? Hotkeys()
    {
        foreach (var valid in new[] { "Ctrl+Alt+W", "Win+F24", "Shift+1", "Ctrl+Alt+,", "Ctrl+Alt+.", "Ctrl+Alt+,+.", "Alt+Ctrl+.+," })
            if (!HotkeyParser.TryParse(valid, out _)) return "合法热键拒绝：" + valid;
        foreach (var invalid in new[] { "W", "Ctrl+Ctrl+A", "Ctrl+汉", "Alt+F25", "Ctrl+A+B", "Ctrl+,+,", "Ctrl+,+.+M" })
            if (HotkeyParser.TryParse(invalid, out _)) return "无效热键通过：" + invalid;
        using var window = new NativeWindow("WeChatNotifier.SelfTest");
        return Native.IsWindow(window.Handle) ? null : "AOT 原生窗口回调无法注册";
    }

    private static string? Icons()
    {
        const int size = 40;
        var pixels = new byte[size * size * 4];
        GdiPlus.Draw(pixels, size, size, graphics =>
            GdiPlus.Image(graphics, IconLoader.ImagePath, new(0, 0, size, size)));
        foreach (var index in new[] { 0, size - 1, (size - 1) * size, size * size - 1 })
            if (pixels[index * 4 + 3] != 0) return "图标四角不透明，可能出现黑边";
        var body = (size / 8 * size + size / 2) * 4;
        if (pixels[body + 3] != 255 || pixels[body + 1] <= pixels[body + 2])
            return "AppIcon.ico 不是微信图标或主体未正确绘制";
        for (var i = 0; i < pixels.Length; i += 4)
            if (pixels[i] > pixels[i + 3] || pixels[i + 1] > pixels[i + 3] || pixels[i + 2] > pixels[i + 3])
                return "图标透明边缘不是预乘 BGRA";
        foreach (var dimension in new[] { 16, 20, 24, 32, 48 })
        {
            var icon = IconLoader.LoadTrayIcon(dimension);
            if (!Native.DestroyIcon(icon)) return "托盘图标资源未释放";
        }
        return null;
    }

    private static string? Glass()
    {
        const int width = 120, height = 64;
        var raw = new byte[width * height * 4];
        for (var i = 0; i < raw.Length; i += 4) { raw[i] = 50; raw[i + 1] = 110; raw[i + 2] = 190; raw[i + 3] = 255; }
        var work = new byte[raw.Length];
        var output = new byte[raw.Length];
        var luma = LiquidGlassRenderer.RenderInto(raw, work, output, width, height, new LiquidGlassRenderer.Settings());
        if (!double.IsFinite(luma) || luma <= 0 || luma >= 1 || output[3] != 0 || output[((height / 2 * width + width / 2) * 4) + 3] != 255)
            return "玻璃透明边缘或亮度无效";
        for (var i = 0; i < output.Length; i += 4)
            if (output[i] > output[i + 3] || output[i + 1] > output[i + 3] || output[i + 2] > output[i + 3])
                return "输出不是预乘 BGRA";
        var config = new AppConfig { GlassBackdrop = false };
        var session = NotificationPresenter.TestSession();
        var physicalWidth = (int)config.Width;
        var physicalHeight = BubbleBitmap.Height(config, session, 1);
        var pixels = BubbleBitmap.Compose(config, session, 1, BubbleBitmap.Solid(config, physicalWidth, physicalHeight, 1),
            physicalWidth, physicalHeight, false, false);
        if (pixels.Length != (physicalWidth + 36) * (physicalHeight + 36) * 4) return "气泡布局尺寸错误";
        var path = Path.Combine(Path.GetTempPath(), "WeChatNotifier-bubble-" + Guid.NewGuid().ToString("N") + ".png");
        try { GdiPlus.SavePixels(pixels, physicalWidth + 36, physicalHeight + 36, path); }
        finally { if (File.Exists(path)) File.Delete(path); }
        return null;
    }
}
