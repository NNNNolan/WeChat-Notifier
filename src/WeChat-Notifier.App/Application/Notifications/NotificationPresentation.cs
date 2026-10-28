using WeChat_Notifier.Configuration;
using WeChat_Notifier.Domain;

namespace WeChat_Notifier.Application.Notifications;

internal sealed record NotificationPresentation(
    SessionInfo Center, SessionInfo? Popup, bool CenterBanner, bool WindowsPopup, bool BubbleSound)
{
    internal static NotificationPresentation Create(SessionInfo session, AppConfig config,
        NotificationMode mode, bool suppressDisplay = false)
    {
        if (suppressDisplay || mode == NotificationMode.Silent)
            return new(session, null, false, false, false);
        if (mode == NotificationMode.Normal && config.UseWindowsNotifications)
            return new(session, null, true, false, false);
        var popup = mode == NotificationMode.Privacy ? new SessionInfo
        {
            Key = session.Key, Title = "微信新消息", Preview = "收到了一条消息", Time = session.Time,
            Muted = session.Muted, Unread = session.Unread, NewMessageCount = session.NewMessageCount
        } : session;
        return new(session, popup, false, config.UseWindowsNotifications,
            !config.UseWindowsNotifications && config.PlaySound);
    }
}
