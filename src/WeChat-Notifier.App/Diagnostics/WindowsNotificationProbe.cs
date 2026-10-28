using System.Diagnostics;
using System.Xml.Linq;
using WeChat_Notifier.Configuration;
using WeChat_Notifier.Domain;
using WeChat_Notifier.Infrastructure.Logging;
using WeChat_Notifier.Infrastructure.Notifications;
using WeChat_Notifier.Infrastructure.WeChat.Database;
using WeChat_Notifier.Infrastructure.Windows;

namespace WeChat_Notifier.Diagnostics;

public static class WindowsNotificationProbe
{
    public static int Run(WindowsNotificationService windows, AppConfig config) =>
        RunAsync(windows, config).GetAwaiter().GetResult();

    private static async Task<int> RunAsync(WindowsNotificationService windows, AppConfig config)
    {
        var id = Guid.NewGuid().ToString("N");
        var readKey = "__probe_" + id + "_read";
        var unreadKey = "__probe_" + id + "_unread";
        var title = "WeChat_Notifier 通知诊断";
        var read = new WeChatSession(readKey, title, "应在同步后撤回", 0, "", 0, false, false);
        var unread = new WeChatSession(unreadKey, title, "应在同步后保留", 0, "", 2, false, false);
        var success = 0;
        var result = 0;
        var cleanupFailed = false;
        long elapsedMs = 0;
        double meanMs = 0;
        double maxMs = 0;

        try
        {
            windows.ValidateAvailable();
            var timer = Stopwatch.StartNew();
            var normal = WindowsNotificationService.CreateNotification(
                new SessionInfo { Key = readKey, Title = title, Preview = read.Summary }, config);
            var suppressed = WindowsNotificationService.CreateNotification(
                new SessionInfo { Key = readKey, Title = title, Preview = read.Summary }, config, true);
            var audio = XElement.Parse(suppressed.Payload).Element("audio");
            if (normal.SuppressDisplay || !suppressed.SuppressDisplay
                || (string?)audio?.Attribute("silent") != "true")
                throw new InvalidOperationException("诊断通知没有同时抑制弹窗和声音");

            windows.Show(new SessionInfo { Key = readKey, Title = title, Preview = read.Summary }, config, true);
            windows.Show(new SessionInfo { Key = unreadKey, Title = title, Preview = unread.Summary }, config, true);
            var tags = new[] { WindowsNotificationService.TagFor(readKey), WindowsNotificationService.TagFor(unreadKey) };
            if ((await windows.GetAllAsync().ConfigureAwait(false)).Count(n => n.Group == "wechat" && tags.Contains(n.Tag)) != 2)
                throw new InvalidOperationException("通知中心未同时找到两条诊断通知");
            success++;

            await windows.SynchronizeReadSessionsAsync(new[] { read, unread }).ConfigureAwait(false);
            var after = await windows.GetAllAsync().ConfigureAwait(false);
            if (after.Any(n => n.Group == "wechat" && n.Tag == tags[0])
                || !after.Any(n => n.Group == "wechat" && n.Tag == tags[1]))
                throw new InvalidOperationException("已读撤回或未读保留结果不正确");
            success++;

            await windows.SynchronizeReadSessionsAsync(new[] { read, unread }).ConfigureAwait(false);
            after = await windows.GetAllAsync().ConfigureAwait(false);
            if (after.Any(n => n.Group == "wechat" && n.Tag == tags[0])
                || !after.Any(n => n.Group == "wechat" && n.Tag == tags[1]))
                throw new InvalidOperationException("重复同步未保持幂等");
            success++;

            var samples = new List<double>(10);
            for (var i = 0; i < 10; i++)
            {
                var sample = Stopwatch.StartNew();
                await windows.SynchronizeReadSessionsAsync(new[] { read, unread }).ConfigureAwait(false);
                samples.Add(sample.Elapsed.TotalMilliseconds);
            }
            meanMs = samples.Average();
            maxMs = samples.Max();
            after = await windows.GetAllAsync().ConfigureAwait(false);
            if (after.Any(n => n.Group == "wechat" && n.Tag == tags[0])
                || !after.Any(n => n.Group == "wechat" && n.Tag == tags[1]))
                throw new InvalidOperationException("重复同步样本改变了通知状态");
            success++;
            timer.Stop();
            elapsedMs = timer.ElapsedMilliseconds;
        }
        catch (Exception ex)
        {
            Log.Error("Windows 通知探针失败", ex);
            Console.Error.WriteLine("通知诊断失败：" + ex.Message);
            result = 1;
        }
        finally
        {
            foreach (var key in new[] { readKey, unreadKey })
            {
                try { await windows.RemoveByTagAndGroupAsync(WindowsNotificationService.TagFor(key), "wechat").ConfigureAwait(false); }
                catch (Exception ex)
                {
                    cleanupFailed = true;
                    Log.Error("清理诊断通知失败", ex);
                    Console.Error.WriteLine("清理诊断通知失败：" + ex.Message);
                }
            }
        }

        if (cleanupFailed) result = 1;
        if (result == 0)
        {
            Console.WriteLine($"通知诊断成功项={success} 耗时={elapsedMs}ms 同步样本=10 平均={meanMs:F2}ms 最大={maxMs:F2}ms（不含运行时初始化） 应用名=微信 诊断标题={title} 图标路径={IconLoader.ImagePath}");
        }
        return result;
    }
}
