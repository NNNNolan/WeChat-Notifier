using WeChat_Notifier.Configuration;
using WeChat_Notifier.Domain;
using WeChat_Notifier.Infrastructure.Notifications;
using WeChat_Notifier.Infrastructure.Windows.Interop;
using WeChat_Notifier.Views.Notifications;

namespace WeChat_Notifier.Application.Notifications;

internal sealed class NotificationPresenter(AppConfig config, ToastManager bubbles, WindowsNotificationService windows)
{
    public void ApplyMode()
    {
        windows.ValidateAvailable();
        if (config.UseWindowsNotifications) bubbles.ClearAll();
    }

    public void Show(SessionInfo session)
    {
        windows.Show(session, config, suppressDisplay: !config.UseWindowsNotifications);
        if (config.UseWindowsNotifications) return;
        bubbles.Show(session);
        if (config.PlaySound && !Native.PlaySound("SystemAsterisk", 0, 0x00010001))
            throw new InvalidOperationException("无法播放通知声音。");
    }

    public void Preview(AppConfig options)
    {
        bubbles.DismissPreview();
        windows.Show(TestSession(), options, suppressDisplay: !options.UseWindowsNotifications);
        if (!options.UseWindowsNotifications) bubbles.ShowPreview(options);
    }

    public void ShowTest() => Show(TestSession());
    internal static SessionInfo TestSession() => new()
    {
        Key = "__test__", Title = "微信通知预览",
        Preview = "这是一条测试通知。点击唤起微信，鼠标悬停暂停自动关闭。",
        Time = DateTime.Now.ToString("HH:mm")
    };
}
