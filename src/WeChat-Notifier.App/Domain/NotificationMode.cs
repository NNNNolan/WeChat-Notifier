namespace WeChat_Notifier.Domain;

public enum NotificationMode
{
    Normal,
    Privacy,
    Silent
}

internal static class NotificationModeParser
{
    internal static NotificationMode Parse(string? value) =>
        Enum.TryParse<NotificationMode>(value, ignoreCase: true, out var mode) && Enum.IsDefined(mode)
            ? mode : throw new InvalidDataException("通知模式必须为 Normal、Privacy 或 Silent。");

    internal static string DisplayName(this NotificationMode mode) => mode switch
    {
        NotificationMode.Normal => "正常模式",
        NotificationMode.Privacy => "隐私模式",
        NotificationMode.Silent => "静默模式",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    internal static NotificationMode Next(this NotificationMode mode) =>
        (NotificationMode)(((int)mode + 1) % 3);
}
