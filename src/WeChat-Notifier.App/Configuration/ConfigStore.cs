using System.Text.Json;
using System.Text;
using WeChat_Notifier.Domain;
using WeChat_Notifier.Infrastructure.Windows.Interop;
using WeChat_Notifier.Infrastructure.Windows;

namespace WeChat_Notifier.Configuration;

/// <summary>无反射的配置读写；无效文件不覆盖，保存通过临时文件原子替换。</summary>
public static class ConfigStore
{
    public static AppConfig Load()
    {
        AppPaths.Ensure();
        if (!File.Exists(AppPaths.ConfigFile))
        {
            var initial = new AppConfig();
            Save(initial);
            return initial;
        }
        var config = JsonSerializer.Deserialize(
            File.ReadAllText(AppPaths.ConfigFile, Encoding.UTF8), AppJsonContext.Default.AppConfig)
            ?? throw new InvalidDataException("配置文件内容不能为 null。");
        Validate(config);
        return config;
    }

    public static void Save(AppConfig config)
    {
        Validate(config);
        AppPaths.Ensure();
        var temporary = AppPaths.ConfigFile + "." + Environment.ProcessId + ".tmp";
        try
        {
            File.WriteAllText(temporary, Serialize(config), new UTF8Encoding(false));
            File.Move(temporary, AppPaths.ConfigFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static string Serialize(AppConfig config) =>
        JsonSerializer.Serialize(config, AppJsonContext.Default.AppConfig);

    public static void CopyInto(AppConfig source, AppConfig target)
    {
        target.Position = source.Position;
        target.Monitor = source.Monitor;
        target.Width = source.Width;
        target.MarginX = source.MarginX;
        target.MarginY = source.MarginY;
        target.Spacing = source.Spacing;
        target.DurationMs = source.DurationMs;
        target.MaxVisible = source.MaxVisible;
        target.UseWindowsNotifications = source.UseWindowsNotifications;
        target.PlaySound = source.PlaySound;
        target.NotificationMode = source.NotificationMode;
        target.ModeSwitchHotkey = source.ModeSwitchHotkey;
        target.ModeSwitchHotkeyEnabled = source.ModeSwitchHotkeyEnabled;
        target.NotifyOnAutomaticModeChange = source.NotifyOnAutomaticModeChange;
        target.SilentApplications = new List<string>(source.SilentApplications);
        target.PrivacyApplications = new List<string>(source.PrivacyApplications);
        target.PollIntervalMs = source.PollIntervalMs;
        target.SkipMuted = source.SkipMuted;
        target.SkipOwnMessages = source.SkipOwnMessages;
        target.IncludeOfficialAccounts = source.IncludeOfficialAccounts;
        target.SuppressForActiveChat = source.SuppressForActiveChat;
        target.SuppressWhenFullscreen = source.SuppressWhenFullscreen;
        target.TrayFlashUntilRead = source.TrayFlashUntilRead;
        target.TrayFlashDurationSeconds = source.TrayFlashDurationSeconds;
        target.SkipSessions = new List<string>(source.SkipSessions);
        target.SkipKeywords = new List<string>(source.SkipKeywords);
        target.WeChatHotkey = source.WeChatHotkey;
        target.WeChatHotkeyEnabled = source.WeChatHotkeyEnabled;
        target.WeChatPath = source.WeChatPath;
        target.Theme = source.Theme;
        target.CornerRadius = source.CornerRadius;
        target.ShadowElevation = source.ShadowElevation;
        target.DynamicBackdrop = source.DynamicBackdrop;
        target.BackdropRefreshMs = source.BackdropRefreshMs;
        target.MaxBodyLines = source.MaxBodyLines;
        target.BackgroundOpacity = source.BackgroundOpacity;
        target.ShowAvatar = source.ShowAvatar;
        target.GlassBackdrop = source.GlassBackdrop;
        target.GlassThickness = source.GlassThickness;
        target.GlassBlur = source.GlassBlur;
        target.GlassRefractiveIndex = source.GlassRefractiveIndex;
        target.GlassChromaticAberration = source.GlassChromaticAberration;
        target.GlassLightIntensity = source.GlassLightIntensity;
        target.GlassAmbientStrength = source.GlassAmbientStrength;
        target.GlassSaturation = source.GlassSaturation;
        target.GlassTint = source.GlassTint;
        target.GlassRefractScale = source.GlassRefractScale;
        target.GlassLightAngle = source.GlassLightAngle;
        target.AutoStart = source.AutoStart;
        target.ShowCloseToTrayHint = source.ShowCloseToTrayHint;
        target.VerboseLog = source.VerboseLog;
    }

    internal static void Validate(AppConfig c)
    {
        static void Range(double value, double min, double max, string name)
        {
            if (!double.IsFinite(value) || value < min || value > max)
                throw new InvalidDataException($"配置项 {name} 必须在 {min}～{max} 之间。");
        }
        if (c.Position is not ("TopRight" or "TopLeft" or "BottomRight" or "BottomLeft")
            || c.Monitor is not ("Primary" or "Mouse") || c.Theme is not ("Light" or "Dark"))
            throw new InvalidDataException("气泡位置、显示器或主题配置无效。");
        if (c.SkipSessions == null || c.SkipKeywords == null
            || c.SkipSessions.Any(string.IsNullOrWhiteSpace) || c.SkipKeywords.Any(string.IsNullOrWhiteSpace)
            || c.WeChatHotkey == null || c.WeChatPath == null)
            throw new InvalidDataException("配置中的列表和路径不能为 null，忽略列表不能含空项。");
        NotificationModeParser.Parse(c.NotificationMode);
        ApplicationRules.Validate(c);
        if (c.WeChatHotkeyEnabled && (!HotkeyParser.TryParse(c.WeChatHotkey, out var wechatHotkey) || wechatHotkey.AdditionalKey != 0))
            throw new InvalidDataException("微信热键应为修饰键加字母、数字、逗号、句号或 F1～F24，例如 Ctrl+Alt+W。");
        if (c.ModeSwitchHotkeyEnabled && !HotkeyParser.TryParse(c.ModeSwitchHotkey, out _))
            throw new InvalidDataException("模式切换热键应为修饰键加字母、数字、逗号、句号、逗号与句号组合或 F1～F24。");
        if (c.ModeSwitchHotkeyEnabled && c.WeChatHotkeyEnabled
            && GlobalHotkeys.SameHotkey(c.WeChatHotkey, c.ModeSwitchHotkey))
            throw new InvalidDataException("模式切换快捷键不能与微信快捷键相同。");
        Range(c.Width, 260, 800, nameof(c.Width));
        Range(c.MarginX, 0, 400, nameof(c.MarginX));
        Range(c.MarginY, 0, 400, nameof(c.MarginY));
        Range(c.Spacing, 0, 60, nameof(c.Spacing));
        Range(c.DurationMs, 800, 60000, nameof(c.DurationMs));
        Range(c.MaxVisible, 1, 12, nameof(c.MaxVisible));
        Range(c.PollIntervalMs, 150, 10000, nameof(c.PollIntervalMs));
        Range(c.TrayFlashDurationSeconds, 1, 300, nameof(c.TrayFlashDurationSeconds));
        Range(c.CornerRadius, 0, 32, nameof(c.CornerRadius));
        Range(c.ShadowElevation, 0, 4, nameof(c.ShadowElevation));
        Range(c.BackdropRefreshMs, 40, 2000, nameof(c.BackdropRefreshMs));
        Range(c.MaxBodyLines, 1, 10, nameof(c.MaxBodyLines));
        Range(c.BackgroundOpacity, 0.3, 1, nameof(c.BackgroundOpacity));
        Range(c.GlassThickness, 0, 60, nameof(c.GlassThickness));
        Range(c.GlassBlur, 0, 24, nameof(c.GlassBlur));
        Range(c.GlassRefractiveIndex, 1, 2, nameof(c.GlassRefractiveIndex));
        Range(c.GlassChromaticAberration, 0, 1, nameof(c.GlassChromaticAberration));
        Range(c.GlassLightIntensity, 0, 3, nameof(c.GlassLightIntensity));
        Range(c.GlassAmbientStrength, 0, 2, nameof(c.GlassAmbientStrength));
        Range(c.GlassSaturation, 0.2, 2, nameof(c.GlassSaturation));
        Range(c.GlassTint, 0, 0.8, nameof(c.GlassTint));
        Range(c.GlassRefractScale, 0, 3, nameof(c.GlassRefractScale));
        Range(c.GlassLightAngle, 0, 360, nameof(c.GlassLightAngle));
    }
}
