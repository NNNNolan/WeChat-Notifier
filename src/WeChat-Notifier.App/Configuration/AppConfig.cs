using WeChat_Notifier.Infrastructure.Windows;
using WeChat_Notifier.Domain;

namespace WeChat_Notifier.Configuration;

/// <summary>用户配置；保存后原位更新，监听服务无需重建。</summary>
public sealed class AppConfig
{
    // 自定义气泡的布局参数。
    public string Position { get; set; } = "TopRight";
    public string Monitor { get; set; } = "Primary";
    public double Width { get; set; } = 384;
    public double MarginX { get; set; } = 14;
    public double MarginY { get; set; } = 14;
    public double Spacing { get; set; } = 10;
    public int DurationMs { get; set; } = 5200;
    public int MaxVisible { get; set; } = 4;

    /// <summary>勾选后显示 Windows 横幅；关闭时显示自定义玻璃气泡，通知中心两种模式都会保留记录。</summary>
    public bool UseWindowsNotifications { get; set; }
    public bool PlaySound { get; set; }
    public string NotificationMode { get; set; } = nameof(Domain.NotificationMode.Normal);
    public string ModeSwitchHotkey { get; set; } = "Ctrl+Alt+,+.";
    public bool ModeSwitchHotkeyEnabled { get; set; } = true;
    public bool NotifyOnAutomaticModeChange { get; set; } = true;
    public List<string> SilentApplications { get; set; } = new();
    public List<string> PrivacyApplications { get; set; } = new();
    /// <summary>文件事件之外的轻量提交头核对间隔；保留旧 JSON 字段名以兼容已有配置。</summary>
    public int PollIntervalMs { get; set; } = 3000;
    public bool SkipMuted { get; set; } = true;
    public bool SkipOwnMessages { get; set; } = true;
    public bool IncludeOfficialAccounts { get; set; }
    public bool SuppressForActiveChat { get; set; } = true;
    public bool SuppressWhenFullscreen { get; set; } = true;
    public bool TrayFlashUntilRead { get; set; } = true;
    public int TrayFlashDurationSeconds { get; set; } = 6;
    public List<string> SkipSessions { get; set; } = new();
    public List<string> SkipKeywords { get; set; } = new();

    /// <summary>应与微信设置中的显示/隐藏快捷键一致；微信在运行时将此键交还给微信。</summary>
    public string WeChatHotkey { get; set; } = "Ctrl+Alt+W";
    public bool WeChatHotkeyEnabled { get; set; } = true;
    public string WeChatPath { get; set; } = "";

    // 外观参数只影响自定义气泡，系统通知外观由 Windows 管理。
    public string Theme { get; set; } = "Light";
    public int CornerRadius { get; set; } = 16;
    public double ShadowElevation { get; set; } = 1.0;
    public bool DynamicBackdrop { get; set; } = true;
    public int BackdropRefreshMs { get; set; } = 140;
    public int MaxBodyLines { get; set; } = 3;
    public double BackgroundOpacity { get; set; } = 0.86;
    public bool ShowAvatar { get; set; } = true;
    public bool GlassBackdrop { get; set; } = true;
    public double GlassThickness { get; set; } = 30;
    public double GlassBlur { get; set; } = 3;
    public double GlassRefractiveIndex { get; set; } = 1.59;
    public double GlassChromaticAberration { get; set; } = 0.3;
    public double GlassLightIntensity { get; set; } = 0.6;
    public double GlassAmbientStrength { get; set; } = 1.0;
    public double GlassSaturation { get; set; } = 0.7;
    public double GlassTint { get; set; } = 0.24;
    public double GlassRefractScale { get; set; } = 1.0;
    public double GlassLightAngle { get; set; } = 135;
    public bool AutoStart { get; set; }
    public bool ShowCloseToTrayHint { get; set; } = true;
    public bool VerboseLog { get; set; }
}
