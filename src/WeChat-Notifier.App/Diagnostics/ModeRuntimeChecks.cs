using System.Diagnostics;
using System.Xml.Linq;
using WeChat_Notifier.Application;
using WeChat_Notifier.Application.Notifications;
using WeChat_Notifier.Configuration;
using WeChat_Notifier.Domain;
using WeChat_Notifier.Infrastructure.Logging;
using WeChat_Notifier.Infrastructure.Notifications;
using WeChat_Notifier.Infrastructure.Windows.Interop;

namespace WeChat_Notifier.Diagnostics;

internal static class ModeRuntimeChecks
{
    internal static async Task RunAsync(ApplicationController controller, string directory)
    {
        Directory.CreateDirectory(directory);
        var messages = new List<string>();
        void Check(bool pass, string message)
        {
            if (!pass) throw new InvalidOperationException(message);
            messages.Add("PASS " + message);
            Log.Info("mode-runtime: " + message);
        }
        var config = controller.Config;
        var original = new AppConfig();
        ConfigStore.CopyInto(config, original);
        var savedConfig = File.ReadAllBytes(AppPaths.ConfigFile);
        var previousNotifications = (await controller.WindowsNotifications.GetAllAsync())
            .Select(n => (n.Tag, n.Group)).ToHashSet();
        var first = Path.Combine(AppPaths.ProgramDirectory, "PrivacyModeFixture.exe");
        var second = Path.Combine(AppPaths.ProgramDirectory, "SilentModeFixture.exe");
        Process? privacy = null, silent = null;
        try
        {
            File.Copy(Environment.ProcessPath!, first, overwrite: true);
            File.Copy(Environment.ProcessPath!, second, overwrite: true);
            config.NotificationMode = "Normal";
            config.SilentApplications = [second];
            config.PrivacyApplications = [first];
            config.NotifyOnAutomaticModeChange = true;
            controller.ModeController.Refresh();
            privacy = StartFixture(first);
            await WaitAsync(() => controller.ModeController.Current == NotificationMode.Privacy);
            Check(controller.Tray.Mode == NotificationMode.Privacy, "隐私 EXE 启动自动切换并更新托盘");
            Check(controller.Bubbles.ActiveCount > 0, "自动模式切换显示本地短暂气泡");
            var modeNotifications = await controller.WindowsNotifications.GetAllAsync();
            Check(!modeNotifications.Any(n => n.Group == "modes" && !previousNotifications.Contains((n.Tag, n.Group))),
                "模式切换提示不写入 Windows 通知中心");
            silent = StartFixture(second);
            await WaitAsync(() => controller.ModeController.Current == NotificationMode.Silent);
            Check(controller.Tray.Mode == NotificationMode.Silent, "静默 EXE 与隐私并行时静默优先");
            silent.Kill();
            await silent.WaitForExitAsync();
            await WaitAsync(() => controller.ModeController.Current == NotificationMode.Privacy);
            Check(true, "静默 EXE 退出恢复仍在运行的隐私模式");
            privacy.Kill();
            await privacy.WaitForExitAsync();
            await WaitAsync(() => controller.ModeController.Current == NotificationMode.Normal);
            Check(true, "隐私 EXE 退出恢复上一个手动模式");
            foreach (var mode in new[] { NotificationMode.Silent, NotificationMode.Privacy, NotificationMode.Normal })
            {
                controller.Tray.ExecuteCommand(10 + (int)mode);
                Check(controller.ModeController.Current == mode && controller.Tray.Mode == mode
                    && ConfigStore.Load().NotificationMode == mode.ToString()
                    && controller.Tray.ModeMenuLabel == $"模式（当前：{mode.DisplayName()}）",
                    "托盘菜单切换并更新一级模式标题：" + mode.DisplayName());
                var menu = controller.Tray.CreateMenu();
                try
                {
                    var submenu = Native.GetSubMenu(menu, 1);
                    Check(submenu != 0 && Enum.GetValues<NotificationMode>().All(item =>
                        Native.GetMenuItemID(submenu, (int)item) == 10 + (uint)item
                        && ((Native.GetMenuState(submenu, 10 + (uint)item, 0) & Native.MF_CHECKED) != 0) == (item == mode)),
                        "模式二级菜单及当前模式勾选：" + mode.DisplayName());
                }
                finally { Native.DestroyMenu(menu); }
            }
            Check(controller.Hotkeys.IsModeHotkeyRegistered, "模式热键实际注册成功");
            Native.PostMessage(controller.Hotkeys.Handle, 0x0312, 2, 0);
            await WaitAsync(() => config.NotificationMode == "Privacy");
            Check(controller.ModeController.Current == NotificationMode.Privacy, "WM_HOTKEY 经过生产入口切换模式");

            config.ModeSwitchHotkey = "Ctrl+Alt+,+.";
            controller.Hotkeys.ReRegister();
            Check(controller.Hotkeys.IsModeHotkeyRegistered, "四键模式快捷键监听建立");
            Native.PostMessage(controller.Hotkeys.Handle, 0x0312, 2, 0);
            await Task.Delay(150);
            Check(config.NotificationMode == "Privacy", "四键监听拒绝旧单键热键消息");
            async Task SendChord(ushort key, ushort additional = 0, bool shift = false)
            {
                var modifiers = shift ? new ushort[] { 0x11, 0x12, 0x10 } : new ushort[] { 0x11, 0x12 };
                if (!await Task.Run(() => Keyboard.Chord(modifiers, key, additionalKey: additional)))
                    throw new InvalidOperationException("无法发送四键回归输入。");
                await Task.Delay(180);
            }
            await SendChord(0xBC, 0xBE);
            Check(config.NotificationMode == "Silent", "实际 Ctrl+Alt+逗号+句号切换一次");
            await SendChord(0xBE, 0xBC);
            Check(config.NotificationMode == "Normal", "标点按下顺序反转仍切换一次");
            await SendChord(0xBC);
            await SendChord(0xBC, 0xBE, shift: true);
            Check(config.NotificationMode == "Normal", "缺少标点或额外 Shift 不触发四键组合");
            controller.Hotkeys.SetRecording(true);
            await SendChord(0xBC, 0xBE);
            Check(config.NotificationMode == "Normal" && !controller.Hotkeys.IsModeHotkeyRegistered,
                "录制期间四键组合不切换模式");
            controller.Hotkeys.SetRecording(false);
            config.ModeSwitchHotkeyEnabled = false;
            controller.Hotkeys.ReRegister();
            await SendChord(0xBC, 0xBE);
            Check(config.NotificationMode == "Normal" && !controller.Hotkeys.IsModeHotkeyRegistered,
                "停用后四键组合不切换模式");
            config.ModeSwitchHotkeyEnabled = true;
            controller.Hotkeys.ReRegister();
            await SendChord(0xBC, 0xBE);
            Check(config.NotificationMode == "Privacy", "恢复监听后四键组合可再次切换");

            controller.Tray.SetUnreadCount(12);
            Check(controller.Tray.IsFlashing && controller.Tray.UnreadCount == 12, "托盘实际更新角标并开始闪烁");
            await Task.Delay(650);
            Check(controller.Tray.IsFlashing && controller.Tray.UnreadCount == 12, "有未读时托盘持续闪烁");
            controller.Tray.SetUnreadCount(0);
            Check(!controller.Tray.IsFlashing && controller.Tray.UnreadCount == 0, "已读后停止闪烁并清零角标");

            var id = "__mode_probe_" + Guid.NewGuid().ToString("N");
            var session = new SessionInfo { Key = id, Title = "回归详情标题", Preview = "仅允许通知中心看到的详细内容", Unread = 1, Time = "12:34" };
            config.UseWindowsNotifications = true;
            var oldId = id + "-old-banner";
            controller.Presenter.Show(new SessionInfo { Key = oldId, Title = session.Title, Preview = session.Preview }, NotificationMode.Normal);
            var originalBanner = (await controller.WindowsNotifications.GetAllAsync())
                .Single(n => n.Tag == WindowsNotificationService.TagFor(oldId));
            controller.Presenter.ApplyMode(NotificationMode.Privacy);
            var retained = (await controller.WindowsNotifications.GetAllAsync())
                .Single(n => n.Tag == WindowsNotificationService.TagFor(oldId));
            var retainedPayload = XElement.Parse(retained.Payload);
            Check(retained.Id != originalBanner.Id && retainedPayload.Descendants("text").Select(t => t.Value).Contains(session.Preview)
                && (string?)retainedPayload.Element("audio")?.Attribute("silent") == "true",
                "进入隐私模式撤回旧详细横幅并保留通知中心详情");
            controller.Presenter.Show(session, NotificationMode.Privacy);
            var notifications = await controller.WindowsNotifications.GetAllAsync();
            var detail = notifications.Single(n => n.Tag == WindowsNotificationService.TagFor(id));
            var popup = notifications.Single(n => n.Tag == WindowsNotificationService.TagFor(id + ":privacy-popup"));
            Check(XElement.Parse(detail.Payload).Descendants("text").Select(t => t.Value).Contains(session.Preview),
                "实际通知中心保留详细消息");
            Check(!popup.Payload.Contains(session.Title) && !popup.Payload.Contains(session.Preview)
                && popup.Payload.Contains("收到了1条消息"), "实际 Windows 隐私横幅内容不含详情");
            var silentId = id + "-silent";
            controller.Presenter.Show(new SessionInfo { Key = silentId, Title = session.Title, Preview = session.Preview }, NotificationMode.Silent);
            notifications = await controller.WindowsNotifications.GetAllAsync();
            Check(notifications.Count(n => n.Tag == WindowsNotificationService.TagFor(silentId)) == 1,
                "静默模式只投递通知中心详细记录");
            Check(!notifications.Any(n => n.Group == "modes" && !previousNotifications.Contains((n.Tag, n.Group))),
                "手动和自动模式提示均不写入 Windows 通知中心");
            await Task.Delay(8500);
            notifications = await controller.WindowsNotifications.GetAllAsync();
            Check(notifications.Any(n => n.Tag == WindowsNotificationService.TagFor(id))
                && !notifications.Any(n => n.Tag == WindowsNotificationService.TagFor(id + ":privacy-popup")),
                "短期隐私横幅到期后通知中心仅保留详情");
            controller.SettingsWindow!.VerifyTitleIcon();
            await controller.SettingsWindow.CapturePagesAsync(directory, verify: true);
            Check(true, "主窗体图标加载及浅深色设置页渲染");
            File.WriteAllLines(Path.Combine(directory, "runtime-results.txt"), messages);
        }
        finally
        {
            foreach (var process in new[] { silent, privacy })
            {
                if (process == null) continue;
                if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
                process.Dispose();
            }
            File.Delete(first);
            File.Delete(second);
            ConfigStore.CopyInto(original, config);
            File.WriteAllBytes(AppPaths.ConfigFile, savedConfig);
            controller.ModeController.Refresh();
            var created = (await controller.WindowsNotifications.GetAllAsync())
                .Where(n => !previousNotifications.Contains((n.Tag, n.Group))).ToArray();
            foreach (var notification in created)
                await controller.WindowsNotifications.RemoveByTagAndGroupAsync(notification.Tag, notification.Group);
        }
    }

    private static Process StartFixture(string path) => Process.Start(new ProcessStartInfo
    {
        FileName = path, ArgumentList = { "--mode-fixture" },
        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
    }) ?? throw new InvalidOperationException("无法启动模式回归进程。");

    private static async Task WaitAsync(Func<bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(6)) throw new TimeoutException("运行时模式验证超时。");
            await Task.Delay(75);
        }
    }
}
