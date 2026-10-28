using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using WeChat_Notifier.Configuration;
using WeChat_Notifier.Domain;
using WeChat_Notifier.Infrastructure.Logging;
using WeChat_Notifier.Infrastructure.WeChat.Database;
using WeChat_Notifier.Infrastructure.Windows;

namespace WeChat_Notifier.Infrastructure.Notifications;

/// <summary>两个通知模式共用同一通知中心，气泡模式的系统记录不弹横幅也不播放声音。</summary>
public sealed class WindowsNotificationService : IDisposable
{
    private const string Group = "wechat";
    private AppNotificationManager? _manager;
    public event Action? Activated;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    public void EnsureRegistered()
    {
        if (_manager != null) return;
        if (!AppNotificationManager.IsSupported())
            throw new InvalidOperationException("无法使用 Windows 通知，请以普通权限运行本程序。");
        Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID("WeChat-Notifier"));
        var manager = AppNotificationManager.Default;
        manager.NotificationInvoked += OnInvoked;
        try
        {
            manager.Register("微信", new Uri(IconLoader.ImagePath));
            _manager = manager;
        }
        catch { manager.NotificationInvoked -= OnInvoked; throw; }
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        if (activation.Kind == ExtendedActivationKind.AppNotification
            && activation.Data is AppNotificationActivatedEventArgs args)
            OnInvoked(manager, args);
    }

    public void ValidateAvailable()
    {
        EnsureRegistered();
        if (_manager!.Setting != AppNotificationSetting.Enabled)
            throw new InvalidOperationException($"Windows 已禁用本程序通知（{_manager.Setting}）。请在系统设置 → 系统 → 通知中启用微信通知。");
    }

    public uint Show(SessionInfo session, AppConfig options, bool suppressDisplay = false)
    {
        ValidateAvailable();
        var notification = CreateNotification(session, options, suppressDisplay);
        _manager!.Show(notification);
        if (notification.Id == 0) throw new InvalidOperationException("Windows 未接受该通知。");
        Log.Info($"windows notification accepted: id={notification.Id}");
        return notification.Id;
    }

    internal static AppNotification CreateNotification(SessionInfo session, AppConfig options, bool suppressDisplay = false) =>
        new(BuildPayload(session, options, suppressDisplay))
        {
            Tag = TagFor(session.Key), Group = Group,
            ExpiresOnReboot = true, Expiration = DateTimeOffset.Now.AddDays(1), SuppressDisplay = suppressDisplay
        };

    internal static string BuildPayload(SessionInfo session, AppConfig options, bool suppressDisplay = false)
    {
        var toast = new XElement("toast", new XAttribute("launch", "action=open"),
            new XElement("visual", new XElement("binding", new XAttribute("template", "ToastGeneric"),
                new XElement("text", session.Title), new XElement("text", session.Preview))));
        if (!options.PlaySound || suppressDisplay) toast.Add(new XElement("audio", new XAttribute("silent", "true")));
        return toast.ToString(SaveOptions.DisableFormatting);
    }

    public async Task SynchronizeReadSessionsAsync(IReadOnlyList<WeChatSession> sessions)
    {
        var read = ReadTagsForSessions(sessions);
        var notifications = await GetAllAsync().ConfigureAwait(false);
        foreach (var tag in FindReadNotificationTags(notifications.Select(n => (n.Tag, n.Group)).ToArray(), read))
            await RemoveByTagAndGroupAsync(tag, Group).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<AppNotification>> GetAllAsync()
    {
        EnsureRegistered();
        return (await _manager!.GetAllAsync().AsTask().ConfigureAwait(false)).ToArray();
    }

    internal async Task RemoveByTagAndGroupAsync(string tag, string group)
    {
        EnsureRegistered();
        await _manager!.RemoveByTagAndGroupAsync(tag, group).AsTask().ConfigureAwait(false);
    }

    internal static HashSet<string> ReadTagsForSessions(IReadOnlyList<WeChatSession> sessions) =>
        sessions.Where(s => s.Unread == 0).Select(s => TagFor(s.Username)).ToHashSet(StringComparer.Ordinal);

    internal static IReadOnlyList<string> FindReadNotificationTags(
        IReadOnlyList<(string Tag, string Group)> notifications, IReadOnlySet<string> readTags) =>
        notifications.Where(n => n.Group == Group && readTags.Contains(n.Tag))
            .Select(n => n.Tag).Distinct(StringComparer.Ordinal).ToArray();

    internal static string TagFor(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];

    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) => Activated?.Invoke();

    public void Dispose()
    {
        if (_manager == null) return;
        _manager.NotificationInvoked -= OnInvoked;
        _manager.Unregister();
        _manager = null;
    }
}
