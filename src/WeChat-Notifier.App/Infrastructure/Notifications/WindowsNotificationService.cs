using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
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
    private const int StackUtf8Limit = 512;
    private static readonly string[] ReadNotificationTagSuffixes =
        ["", ":popup", ":privacy-popup", ":privacy-detail", ":silent"];
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

    internal void SuppressPopups()
    {
        // GetAll 不保留 SuppressDisplay 这一投递参数；按组撤回，不能依赖其返回的默认值。
        // 同步完成撤回，模式切换返回前不能留下旧的详细横幅。
        foreach (var notification in GetAllAsync().GetAwaiter().GetResult()
            .Where(n => n.Group == Group))
        {
            var replacement = WithoutPopup(notification);
            RemoveByTagAndGroupAsync(notification.Tag, notification.Group).GetAwaiter().GetResult();
            if (replacement.Expiration <= DateTimeOffset.Now) continue;
            _manager!.Show(replacement);
            if (replacement.Id == 0) throw new InvalidOperationException("Windows 未接受模式切换后的通知中心记录。");
        }
    }

    internal static AppNotification WithoutPopup(AppNotification original)
    {
        var payload = XElement.Parse(original.Payload);
        payload.Element("audio")?.Remove();
        payload.Add(new XElement("audio", new XAttribute("silent", "true")));
        return new(payload.ToString(SaveOptions.DisableFormatting))
        {
            Tag = original.Tag, Group = original.Group, Expiration = original.Expiration,
            ExpiresOnReboot = original.ExpiresOnReboot, SuppressDisplay = true
        };
    }

    public uint Show(SessionInfo session, AppConfig options, bool suppressDisplay = false,
        string? tagKey = null, TimeSpan? expiresAfter = null)
    {
        ValidateAvailable();
        var notification = CreateNotification(session, options, suppressDisplay, tagKey);
        if (expiresAfter.HasValue) notification.Expiration = DateTimeOffset.Now.Add(expiresAfter.Value);
        _manager!.Show(notification);
        if (notification.Id == 0) throw new InvalidOperationException("Windows 未接受该通知。");
        Log.Info($"windows notification accepted: id={notification.Id}");
        return notification.Id;
    }

    internal static AppNotification CreateNotification(SessionInfo session, AppConfig options,
        bool suppressDisplay = false, string? tagKey = null) =>
        new(BuildPayload(session, options, suppressDisplay))
        {
            Tag = TagFor(tagKey ?? session.Key), Group = Group,
            ExpiresOnReboot = true, Expiration = DateTimeOffset.Now.AddDays(1), SuppressDisplay = suppressDisplay
        };

    internal static string BuildPayload(SessionInfo session, AppConfig options, bool suppressDisplay = false)
    {
        var launch = "action=open;messageCount=" + Math.Max(1, session.NewMessageCount).ToString(CultureInfo.InvariantCulture);
        var toast = new XElement("toast", new XAttribute("launch", launch),
            new XElement("visual", new XElement("binding", new XAttribute("template", "ToastGeneric"),
                new XElement("text", session.Title), new XElement("text", session.Preview))));
        if (!options.PlaySound || suppressDisplay) toast.Add(new XElement("audio", new XAttribute("silent", "true")));
        return toast.ToString(SaveOptions.DisableFormatting);
    }

    public async Task SynchronizeReadSessionsAsync(IReadOnlyList<WeChatSession> sessions)
    {
        var notifications = await GetAllAsync().ConfigureAwait(false);
        if (notifications.Count == 0) return;

        var existing = new Dictionary<ulong, string>();
        foreach (var notification in notifications)
        {
            if (notification.Group == Group
                && ulong.TryParse(notification.Tag, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var tag))
                existing.TryAdd(tag, notification.Tag);
        }

        foreach (var session in sessions)
        {
            if (session.Unread != 0) continue;
            foreach (var suffix in ReadNotificationTagSuffixes)
            {
                if (!existing.Remove(TagHash(session.Username, suffix), out var tag)) continue;
                await RemoveByTagAndGroupAsync(tag, Group).ConfigureAwait(false);
            }
        }
    }

    internal async Task<IReadOnlyList<AppNotification>> GetAllAsync()
    {
        EnsureRegistered();
        return (await _manager!.GetAllAsync().AsTask().ConfigureAwait(false)).ToArray();
    }

    internal int ExistingMessageCount(string tagKey)
    {
        var tag = TagFor(tagKey);
        var existing = GetAllAsync().GetAwaiter().GetResult()
            .FirstOrDefault(notification => notification.Group == Group && notification.Tag == tag);
        if (existing == null) return 0;

        var launch = (string?)XElement.Parse(existing.Payload).Attribute("launch");
        var countField = launch?.Split(';').FirstOrDefault(part =>
            part.StartsWith("messageCount=", StringComparison.Ordinal));
        if (countField == null) return 1;

        var countText = countField["messageCount=".Length..];
        if (!int.TryParse(countText, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 1)
            throw new InvalidDataException("通知中心中的消息数格式无效。");
        return count;
    }

    internal async Task RemoveByTagAndGroupAsync(string tag, string group)
    {
        EnsureRegistered();
        await _manager!.RemoveByTagAndGroupAsync(tag, group).AsTask().ConfigureAwait(false);
    }

    internal static HashSet<string> ReadTagsForSessions(IReadOnlyList<WeChatSession> sessions)
    {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            if (session.Unread != 0) continue;
            foreach (var suffix in ReadNotificationTagSuffixes)
                tags.Add(TagFor(session.Username, suffix));
        }
        return tags;
    }

    internal static IReadOnlyList<string> FindReadNotificationTags(
        IReadOnlyList<(string Tag, string Group)> notifications, IReadOnlySet<string> readTags) =>
        notifications.Where(n => n.Group == Group && readTags.Contains(n.Tag))
            .Select(n => n.Tag).Distinct(StringComparer.Ordinal).ToArray();

    internal static string TagFor(string key) => TagFor(key, string.Empty);

    private static string TagFor(string key, string suffix)
    {
        Span<byte> hash = stackalloc byte[32];
        HashTag(key, suffix, hash);
        return Convert.ToHexString(hash[..8]);
    }

    private static ulong TagHash(string key, string suffix)
    {
        Span<byte> hash = stackalloc byte[32];
        HashTag(key, suffix, hash);
        return BinaryPrimitives.ReadUInt64BigEndian(hash);
    }

    private static void HashTag(string key, string suffix, Span<byte> hash)
    {
        var byteCount = checked(Encoding.UTF8.GetByteCount(key.AsSpan()) + Encoding.UTF8.GetByteCount(suffix.AsSpan()));
        if (byteCount <= StackUtf8Limit)
        {
            Span<byte> input = stackalloc byte[byteCount];
            ComputeTagHash(key, suffix, input, hash);
            return;
        }

        var rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            ComputeTagHash(key, suffix, rented.AsSpan(0, byteCount), hash);
        }
        finally
        {
            rented.AsSpan(0, byteCount).Clear();
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void ComputeTagHash(string key, string suffix, Span<byte> input, Span<byte> hash)
    {
        var written = Encoding.UTF8.GetBytes(key.AsSpan(), input);
        Encoding.UTF8.GetBytes(suffix.AsSpan(), input[written..]);
        SHA256.HashData(input, hash);
    }

    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) => Activated?.Invoke();

    public void Dispose()
    {
        if (_manager == null) return;
        _manager.NotificationInvoked -= OnInvoked;
        _manager.Unregister();
        _manager = null;
    }
}
