using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using WeChat_Notifier.Configuration;

namespace WeChat_Notifier.ViewModels;

/// <summary>隔离的设置工作副本；与打开时快照比较，派生未保存项数。</summary>
public sealed partial class SettingsViewModel : INotifyPropertyChanged
{
    private static readonly string[] Positions = { "TopRight", "TopLeft", "BottomRight", "BottomLeft" };
    private static readonly string[] Monitors = { "Primary", "Mouse" };
    private static readonly string[] Themes = { "Dark", "Light" };

    /// <summary>只有外观选项触发实时气泡预览。</summary>
    public static readonly IReadOnlySet<string> AppearanceOptions = new HashSet<string>
    {
        nameof(Width),
        nameof(MaxVisible),
        nameof(Spacing),
        nameof(MarginX),
        nameof(MarginY),
        nameof(MaxBodyLines),
        nameof(PositionIndex),
        nameof(MonitorIndex),
        nameof(ThemeIndex),
        nameof(GlassBackdrop),
        nameof(DynamicBackdrop),
        nameof(BackdropRefreshMs),
        nameof(BackgroundOpacity),
        nameof(CornerRadius),
        nameof(ShadowElevation),
        nameof(ShowAvatar),
        nameof(GlassThickness),
        nameof(GlassBlur),
        nameof(GlassRefractiveIndex),
        nameof(GlassChromaticAberration),
        nameof(GlassRefractScale),
        nameof(GlassSaturation),
        nameof(GlassTint),
        nameof(GlassLightIntensity),
        nameof(GlassAmbientStrength),
        nameof(GlassLightAngle),
    };

    private readonly AppConfig _c;
    private readonly AppConfig _original;

    private bool _isDirty;
    private int _dirtyCount;

    public SettingsViewModel(AppConfig source)
    {
        _c = new AppConfig();
        ConfigStore.CopyInto(source, _c);
        _original = new AppConfig();
        ConfigStore.CopyInto(_c, _original);
    }

    /// <summary>The live working copy. Handed to ConfigStore on save.</summary>
    public AppConfig Model => _c;

    public event PropertyChangedEventHandler? PropertyChanged;

    // ------------------------------------------------------------------ dirty state

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (_isDirty == value) return;
            _isDirty = value;
            Raise(nameof(IsDirty));
        }
    }

    /// <summary>How many options differ from the config the dialog opened with.</summary>
    public int DirtyCount
    {
        get => _dirtyCount;
        private set
        {
            if (_dirtyCount == value) return;
            _dirtyCount = value;
            Raise(nameof(DirtyCount));
        }
    }

    private void RefreshDirty()
    {
        var current = JsonSerializer.SerializeToElement(_c, AppJsonContext.Default.AppConfig);
        var original = JsonSerializer.SerializeToElement(_original, AppJsonContext.Default.AppConfig);
        DirtyCount = current.EnumerateObject().Count(p =>
            !JsonElement.DeepEquals(p.Value, original.GetProperty(p.Name)));
        IsDirty = DirtyCount > 0;
    }

    public void Revert()
    {
        ConfigStore.CopyInto(_original, _c);
        RaiseAll();
    }

    public void ResetToDefaults()
    {
        ConfigStore.CopyInto(new AppConfig(), _c);
        RaiseAll();
    }

    private void RaiseAll()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        RefreshDirty();
    }

    /// <summary>只校验工作副本；持久化与开机启动由应用保存入口统一负责。</summary>
    public void Commit()
    {
        _c.WeChatHotkey = _c.WeChatHotkey.Trim();
        _c.WeChatPath = _c.WeChatPath.Trim();

        ConfigStore.Validate(_c);
    }

    // ------------------------------------------------------------------ plumbing

    private bool Set<T>(T current, T value, Action<T> write, string name)
    {
        // Doubles come from sliders, whose tick snapping leaves float noise in the
        // low bits. Store a rounded value so the config file and the dirty check
        // both see clean numbers.
        if (typeof(T) == typeof(double))
        {
            var c = (double)(object)current!;
            var v = Math.Round((double)(object)value!, 4);
            if (Math.Abs(c - v) < 1e-9) return false;
            write((T)(object)v);
            Raise(name);
            RefreshDirty();
            return true;
        }

        if (EqualityComparer<T>.Default.Equals(current, value)) return false;
        write(value);
        Raise(name);
        RefreshDirty();
        return true;
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ------------------------------------------------------------------ placement

    public double Width
    {
        get => _c.Width;
        set => Set(_c.Width, value, v => _c.Width = v, nameof(Width));
    }

    public double MaxVisible
    {
        get => _c.MaxVisible;
        set => Set((double)_c.MaxVisible, value, v => _c.MaxVisible = (int)Math.Round(v), nameof(MaxVisible));
    }

    public double Spacing
    {
        get => _c.Spacing;
        set => Set(_c.Spacing, value, v => _c.Spacing = v, nameof(Spacing));
    }

    public double MarginX
    {
        get => _c.MarginX;
        set => Set(_c.MarginX, value, v => _c.MarginX = v, nameof(MarginX));
    }

    public double MarginY
    {
        get => _c.MarginY;
        set => Set(_c.MarginY, value, v => _c.MarginY = v, nameof(MarginY));
    }

    public double MaxBodyLines
    {
        get => _c.MaxBodyLines;
        set => Set((double)_c.MaxBodyLines, value, v => _c.MaxBodyLines = (int)Math.Round(v), nameof(MaxBodyLines));
    }

    public double DurationMs
    {
        get => _c.DurationMs;
        set => Set((double)_c.DurationMs, value, v => _c.DurationMs = (int)Math.Round(v), nameof(DurationMs));
    }

    public void AcceptChanges()
    {
        ConfigStore.CopyInto(_c, _original);
        RefreshDirty();
    }

    internal void SynchronizeCloseToTrayHint(bool show)
    {
        _c.ShowCloseToTrayHint = _original.ShowCloseToTrayHint = show;
        RefreshDirty();
    }

    public int PositionIndex
    {
        get { var i = Array.IndexOf(Positions, _c.Position); return i < 0 ? 0 : i; }
        set
        {
            var idx = Math.Clamp(value, 0, Positions.Length - 1);
            Set(_c.Position, Positions[idx], v => _c.Position = v, nameof(PositionIndex));
        }
    }

    public int MonitorIndex
    {
        get { var i = Array.IndexOf(Monitors, _c.Monitor); return i < 0 ? 0 : i; }
        set
        {
            var idx = Math.Clamp(value, 0, Monitors.Length - 1);
            Set(_c.Monitor, Monitors[idx], v => _c.Monitor = v, nameof(MonitorIndex));
        }
    }

    public int ThemeIndex
    {
        get { var i = Array.IndexOf(Themes, _c.Theme); return i < 0 ? 0 : i; }
        set
        {
            var idx = Math.Clamp(value, 0, Themes.Length - 1);
            Set(_c.Theme, Themes[idx], v => _c.Theme = v, nameof(ThemeIndex));
        }
    }

    // ------------------------------------------------------------------ behaviour

    /// <summary>通知模式变化同时更新外观区域的可用状态，不提前写入正式配置。</summary>
    public bool UseWindowsNotifications
    {
        get => _c.UseWindowsNotifications;
        set
        {
            if (Set(_c.UseWindowsNotifications, value, v => _c.UseWindowsNotifications = v, nameof(UseWindowsNotifications)))
                Raise(nameof(UseCustomBubble));
        }
    }

    public bool UseCustomBubble => !_c.UseWindowsNotifications;

    public bool PlaySound
    {
        get => _c.PlaySound;
        set => Set(_c.PlaySound, value, v => _c.PlaySound = v, nameof(PlaySound));
    }

    public bool SkipMuted
    {
        get => _c.SkipMuted;
        set => Set(_c.SkipMuted, value, v => _c.SkipMuted = v, nameof(SkipMuted));
    }

    public bool SkipOwnMessages
    {
        get => _c.SkipOwnMessages;
        set => Set(_c.SkipOwnMessages, value, v => _c.SkipOwnMessages = v, nameof(SkipOwnMessages));
    }

    public bool IncludeOfficialAccounts
    {
        get => _c.IncludeOfficialAccounts;
        set => Set(_c.IncludeOfficialAccounts, value, v => _c.IncludeOfficialAccounts = v, nameof(IncludeOfficialAccounts));
    }

    public bool SuppressForActiveChat
    {
        get => _c.SuppressForActiveChat;
        set => Set(_c.SuppressForActiveChat, value, v => _c.SuppressForActiveChat = v, nameof(SuppressForActiveChat));
    }

    public bool SuppressWhenFullscreen
    {
        get => _c.SuppressWhenFullscreen;
        set => Set(_c.SuppressWhenFullscreen, value, v => _c.SuppressWhenFullscreen = v, nameof(SuppressWhenFullscreen));
    }

    public bool WeChatHotkeyEnabled
    {
        get => _c.WeChatHotkeyEnabled;
        set => Set(_c.WeChatHotkeyEnabled, value, v => _c.WeChatHotkeyEnabled = v, nameof(WeChatHotkeyEnabled));
    }

    public bool AutoStart
    {
        get => _c.AutoStart;
        set => Set(_c.AutoStart, value, v => _c.AutoStart = v, nameof(AutoStart));
    }

    /// <summary>Comma-separated editor for <see cref="AppConfig.SkipSessions"/>.</summary>
    public string SkipSessionsText
    {
        get => string.Join(", ", _c.SkipSessions);
        set => Set(GetSkipSessionsText(), value, v => _c.SkipSessions = SplitList(v), nameof(SkipSessionsText));
    }

    public string SkipKeywordsText
    {
        get => string.Join(", ", _c.SkipKeywords);
        set => Set(GetSkipKeywordsText(), value, v => _c.SkipKeywords = SplitList(v), nameof(SkipKeywordsText));
    }

    private string GetSkipSessionsText() => string.Join(", ", _c.SkipSessions);
    private string GetSkipKeywordsText() => string.Join(", ", _c.SkipKeywords);

    private static List<string> SplitList(string text) =>
        text.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToList();

    // ------------------------------------------------------------------ look

    public bool GlassBackdrop
    {
        get => _c.GlassBackdrop;
        set => Set(_c.GlassBackdrop, value, v => _c.GlassBackdrop = v, nameof(GlassBackdrop));
    }

    public bool DynamicBackdrop
    {
        get => _c.DynamicBackdrop;
        set => Set(_c.DynamicBackdrop, value, v => _c.DynamicBackdrop = v, nameof(DynamicBackdrop));
    }

    public double BackdropRefreshMs
    {
        get => _c.BackdropRefreshMs;
        set => Set((double)_c.BackdropRefreshMs, value, v => _c.BackdropRefreshMs = (int)Math.Round(v), nameof(BackdropRefreshMs));
    }

    public double BackgroundOpacity
    {
        get => _c.BackgroundOpacity;
        set => Set(_c.BackgroundOpacity, value, v => _c.BackgroundOpacity = v, nameof(BackgroundOpacity));
    }

    public double CornerRadius
    {
        get => _c.CornerRadius;
        set => Set(_c.CornerRadius, value, v => _c.CornerRadius = (int)Math.Round(v), nameof(CornerRadius));
    }

    public double ShadowElevation
    {
        get => _c.ShadowElevation;
        set => Set(_c.ShadowElevation, value, v => _c.ShadowElevation = v, nameof(ShadowElevation));
    }

    public bool ShowAvatar
    {
        get => _c.ShowAvatar;
        set => Set(_c.ShowAvatar, value, v => _c.ShowAvatar = v, nameof(ShowAvatar));
    }

    // ------------------------------------------------------------------ hotkeys

    public string WeChatHotkey
    {
        get => _c.WeChatHotkey;
        set => Set(_c.WeChatHotkey, value, v => _c.WeChatHotkey = v, nameof(WeChatHotkey));
    }

    public string WeChatPath
    {
        get => _c.WeChatPath;
        set => Set(_c.WeChatPath, value, v => _c.WeChatPath = v, nameof(WeChatPath));
    }

    // ------------------------------------------------------------------ advanced

    public double PollIntervalMs
    {
        get => _c.PollIntervalMs;
        set => Set((double)_c.PollIntervalMs, value, v => _c.PollIntervalMs = (int)Math.Round(v), nameof(PollIntervalMs));
    }

    public bool VerboseLog
    {
        get => _c.VerboseLog;
        set => Set(_c.VerboseLog, value, v => _c.VerboseLog = v, nameof(VerboseLog));
    }

    public double GlassThickness
    {
        get => _c.GlassThickness;
        set => Set(_c.GlassThickness, value, v => _c.GlassThickness = v, nameof(GlassThickness));
    }

    public double GlassBlur
    {
        get => _c.GlassBlur;
        set => Set(_c.GlassBlur, value, v => _c.GlassBlur = v, nameof(GlassBlur));
    }

    public double GlassRefractiveIndex
    {
        get => _c.GlassRefractiveIndex;
        set => Set(_c.GlassRefractiveIndex, value, v => _c.GlassRefractiveIndex = v, nameof(GlassRefractiveIndex));
    }

    public double GlassChromaticAberration
    {
        get => _c.GlassChromaticAberration;
        set => Set(_c.GlassChromaticAberration, value, v => _c.GlassChromaticAberration = v, nameof(GlassChromaticAberration));
    }

    public double GlassSaturation
    {
        get => _c.GlassSaturation;
        set => Set(_c.GlassSaturation, value, v => _c.GlassSaturation = v, nameof(GlassSaturation));
    }

    public double GlassTint
    {
        get => _c.GlassTint;
        set => Set(_c.GlassTint, value, v => _c.GlassTint = v, nameof(GlassTint));
    }

    public double GlassLightIntensity
    {
        get => _c.GlassLightIntensity;
        set => Set(_c.GlassLightIntensity, value, v => _c.GlassLightIntensity = v, nameof(GlassLightIntensity));
    }

    public double GlassAmbientStrength
    {
        get => _c.GlassAmbientStrength;
        set => Set(_c.GlassAmbientStrength, value, v => _c.GlassAmbientStrength = v, nameof(GlassAmbientStrength));
    }

    public double GlassLightAngle
    {
        get => _c.GlassLightAngle;
        set => Set(_c.GlassLightAngle, value, v => _c.GlassLightAngle = v, nameof(GlassLightAngle));
    }

    public double GlassRefractScale
    {
        get => _c.GlassRefractScale;
        set => Set(_c.GlassRefractScale, value, v => _c.GlassRefractScale = v, nameof(GlassRefractScale));
    }

    // ------------------------------------------------------------------ about

    public string Version
    {
        get
        {
            var v = typeof(SettingsViewModel).Assembly.GetName().Version;
            return v == null
                ? "1.0.0"
                : string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}", v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    public string ConfigPath => AppPaths.ConfigFile;
    public string LogPath => AppPaths.LogFile;
}
