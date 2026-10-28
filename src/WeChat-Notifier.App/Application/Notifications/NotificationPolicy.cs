using WeChat_Notifier.Configuration;
using WeChat_Notifier.Domain;

namespace WeChat_Notifier.Application.Notifications;

internal enum NotificationDisposition { Ignore, Silent, Show }

/// <summary>检测和投递任一时刻在微信前台就不补弹；全屏只记录到通知中心。</summary>
internal static class NotificationPolicy
{
    internal static NotificationDisposition Decide(AppConfig config, WeChatWindowState detected,
        WeChatWindowState delivery, bool fullscreen)
    {
        if (detected == WeChatWindowState.NotRunning || delivery == WeChatWindowState.NotRunning)
            return NotificationDisposition.Ignore;
        if (config.SuppressForActiveChat
            && (detected == WeChatWindowState.Foreground || delivery == WeChatWindowState.Foreground))
            return NotificationDisposition.Ignore;
        return config.SuppressWhenFullscreen && fullscreen ? NotificationDisposition.Silent : NotificationDisposition.Show;
    }
}
