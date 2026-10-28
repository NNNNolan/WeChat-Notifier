using WeChat_Notifier.Configuration;
using WeChat_Notifier.Domain;
using WeChat_Notifier.Infrastructure.Notifications;
using WeChat_Notifier.Infrastructure.Windows.Interop;
using WeChat_Notifier.Views.Notifications;

namespace WeChat_Notifier.Application.Notifications;

internal sealed class NotificationPresenter(AppConfig config, ToastManager bubbles, WindowsNotificationService windows)
{
    public void ApplyMode(NotificationMode mode)
    {
        windows.ValidateAvailable();
        // 模式变化时清除旧的详细气泡，避免隐私模式下旧内容继续显示。
        bubbles.ClearAll();
        if (mode != NotificationMode.Normal) windows.SuppressPopups();
    }

    public void Show(SessionInfo session, NotificationMode mode, bool suppressDisplay = false) =>
        Show(session, config, mode, suppressDisplay);

    private void Show(SessionInfo session, AppConfig options, NotificationMode mode, bool suppressDisplay = false,
        bool preview = false)
    {
        var plan = NotificationPresentation.Create(session, options, mode, suppressDisplay);
        windows.Show(plan.Center, options, suppressDisplay: !plan.CenterBanner);
        if (plan.Popup == null) return;
        if (plan.WindowsPopup)
        {
            // 短期泛化横幅与长期详细记录分开；详细内容从不进入横幅。
            windows.Show(plan.Popup, options, tagKey: session.Key + ":privacy-popup",
                expiresAfter: TimeSpan.FromSeconds(8));
        }
        else if (preview) bubbles.ShowPreview(options, plan.Popup);
        else bubbles.Show(plan.Popup);
        if (plan.BubbleSound && !Native.PlaySound("SystemAsterisk", 0, 0x00010001))
            throw new InvalidOperationException("无法播放通知声音。");
    }

    public void Preview(AppConfig options, NotificationMode mode)
    {
        bubbles.DismissPreview();
        Show(TestSession(), options, mode, preview: true);
    }

    internal static SessionInfo TestSession() => new()
    {
        Key = "__test__", Title = "微信通知预览",
        Preview = "这是一条测试通知。点击唤起微信，鼠标悬停暂停自动关闭。",
        Time = DateTime.Now.ToString("HH:mm")
    };
}
