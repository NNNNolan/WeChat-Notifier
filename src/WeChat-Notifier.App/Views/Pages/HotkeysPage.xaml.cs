using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml;
using VirtualKey = Windows.System.VirtualKey;
using Windows.Storage.Pickers;
using WeChat_Notifier.Infrastructure.WeChat;
using WeChat_Notifier.Infrastructure.Windows.Interop;
using WeChat_Notifier.Infrastructure.Windows;
using WeChat_Notifier.ViewModels;
using WeChat_Notifier.Views;

namespace WeChat_Notifier.Views.Pages;

public sealed partial class HotkeysPage : Page
{
    private readonly MainWindow _window;
    private readonly GlobalHotkeys _hotkeys;
    private readonly DispatcherQueueTimer _timer;
    private TextBox? _recordingInput;
    private ushort _pendingKey, _pendingOtherKey;
    private uint _pendingModifiers;
    public SettingsViewModel ViewModel { get; }

    internal HotkeysPage(SettingsViewModel viewModel, MainWindow window, GlobalHotkeys hotkeys)
    {
        ViewModel = viewModel;
        _window = window;
        _hotkeys = hotkeys;
        InitializeComponent();
        // 输入框保留焦点时再次点击也能开始录制；TextBox 会处理普通指针事件。
        HotkeyInput.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnHotkeyPointerPressed), true);
        ModeHotkeyInput.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnHotkeyPointerPressed), true);
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(500);
        _timer.Tick += (_, _) => RefreshStatus();
        Loaded += (_, _) => { RefreshStatus(); _timer.Start(); };
    }

    private void RefreshStatus()
    {
        if (_recordingInput != HotkeyInput) HotkeyStatus.Text = _hotkeys.StatusText;
        if (_recordingInput != ModeHotkeyInput) ModeHotkeyStatus.Text = _hotkeys.ModeStatusText;
    }

    internal void CheckBindings()
    {
        if (ModeHotkeyInput.Text != ViewModel.ModeSwitchHotkey)
            throw new InvalidOperationException("模式快捷键设置没有绑定。");
        var original = ViewModel.ModeSwitchHotkey;
        var enabled = ViewModel.ModeSwitchHotkeyEnabled;
        var wechatEnabled = ViewModel.WeChatHotkeyEnabled;
        var registered = _hotkeys.IsModeHotkeyRegistered;
        try
        {
            ViewModel.WeChatHotkeyEnabled = false;
            BeginRecording(ModeHotkeyInput);
            if (_hotkeys.IsModeHotkeyRegistered) throw new InvalidOperationException("录制时未释放模式热键。");
            RecordKey(VirtualKey.F24, 7);
            if (ViewModel.ModeSwitchHotkey != "Ctrl+Alt+Shift+F24" || ModeHotkeyInput.Text != ViewModel.ModeSwitchHotkey || _recordingInput != null
                || _hotkeys.IsModeHotkeyRegistered != registered)
                throw new InvalidOperationException("模式键位录制或结束后热键恢复失败。");
            BeginRecording(ModeHotkeyInput);
            RecordKey((VirtualKey)0xBC, 3);
            RecordKey((VirtualKey)0xBE, 3);
            RecordKeyReleased((VirtualKey)0xBE);
            if (ViewModel.ModeSwitchHotkey != "Ctrl+Alt+,+.")
                throw new InvalidOperationException("四键组合录制失败。");
            BeginRecording(ModeHotkeyInput);
            RecordKey((VirtualKey)0xBE, 3);
            RecordKey((VirtualKey)0xBC, 3);
            RecordKeyReleased((VirtualKey)0xBC);
            if (ViewModel.ModeSwitchHotkey != "Ctrl+Alt+,+.")
                throw new InvalidOperationException("反序按下双主键未规范化。");
            BeginRecording(ModeHotkeyInput);
            RecordKey(VirtualKey.Escape, 0);
            if (ViewModel.ModeSwitchHotkey != "Ctrl+Alt+,+.")
                throw new InvalidOperationException("取消录制更改了快捷键。");
            BeginRecording(ModeHotkeyInput);
            RecordKey(VirtualKey.Back, 0);
            if (ViewModel.ModeSwitchHotkey.Length != 0 || ViewModel.ModeSwitchHotkeyEnabled)
                throw new InvalidOperationException("清除模式快捷键未同时停用。");
        }
        finally
        {
            StopRecording();
            ViewModel.ModeSwitchHotkey = original;
            ViewModel.ModeSwitchHotkeyEnabled = enabled;
            ViewModel.WeChatHotkeyEnabled = wechatEnabled;
        }
    }

    private void OnHotkeyGotFocus(object sender, RoutedEventArgs e)
    {
        BeginRecording((TextBox)sender);
    }

    private void OnHotkeyPointerPressed(object sender, PointerRoutedEventArgs e) => BeginRecording((TextBox)sender);

    private void BeginRecording(TextBox input)
    {
        _pendingKey = _pendingOtherKey = 0;
        _recordingInput = input;
        _hotkeys.SetRecording(true);
        RefreshStatus();
        (input == ModeHotkeyInput ? ModeHotkeyStatus : HotkeyStatus).Text = "正在录制，原热键已临时释放";
    }

    private void StopRecording()
    {
        if (_recordingInput == null) return;
        _recordingInput = null;
        _pendingKey = _pendingOtherKey = 0;
        _hotkeys.SetRecording(false);
        RefreshStatus();
    }

    private void OnHotkeyLostFocus(object sender, RoutedEventArgs e) => StopRecording();
    private void OnUnloaded(object sender, RoutedEventArgs e) { _timer.Stop(); StopRecording(); }

    private void OnHotkeyKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox input || _recordingInput != input || e.Key == VirtualKey.Tab) return;
        e.Handled = true;
        try
        {
            var modifiers = (Down(0x12) ? 1u : 0) | (Down(0x11) ? 2u : 0)
                | (Down(0x10) ? 4u : 0) | (Down(0x5B) || Down(0x5C) ? 8u : 0);
            RecordKey(e.Key, modifiers);
        }
        catch (Exception ex) { StopRecording(); _window.ReportError("无法录制快捷键", ex.Message); }
    }

    private void RecordKey(VirtualKey key, uint modifiers)
    {
        if (_recordingInput == null) return;
        var mode = _recordingInput == ModeHotkeyInput;
        var status = mode ? ModeHotkeyStatus : HotkeyStatus;
        if (key == VirtualKey.Escape) { StopRecording(); return; }
        if (key == VirtualKey.Back)
        {
            if (mode) { ViewModel.ModeSwitchHotkeyEnabled = false; ViewModel.ModeSwitchHotkey = ""; }
            else { ViewModel.WeChatHotkeyEnabled = false; ViewModel.WeChatHotkey = ""; }
            StopRecording();
            return;
        }
        var value = (int)key;
        if (value is not (>= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x70 and <= 0x87 or 0xBC or 0xBE)) return;
        if (modifiers == 0) { status.Text = "请至少按住一个修饰键"; return; }
        if (value is 0xBC or 0xBE)
        {
            if (_pendingKey == 0) { _pendingKey = (ushort)value; _pendingModifiers = modifiers; }
            else if (mode && _pendingKey != value && _pendingModifiers == modifiers) _pendingOtherKey = (ushort)value;
            status.Text = _pendingOtherKey != 0 ? "已识别逗号与句号组合，松开标点键完成录制"
                : mode ? "松开标点键完成录制，也可同时按住逗号与句号" : "松开标点键完成录制";
            return;
        }
        SaveRecordedChord(value, modifiers);
    }

    private void OnHotkeyKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox input || _recordingInput != input) return;
        try { RecordKeyReleased(e.Key); }
        catch (Exception ex) { StopRecording(); _window.ReportError("无法录制快捷键", ex.Message); }
    }

    private void RecordKeyReleased(VirtualKey key)
    {
        if (_pendingKey == 0 || ((int)key != _pendingKey && (int)key != _pendingOtherKey)) return;
        var primary = _pendingKey;
        var both = _pendingOtherKey != 0;
        _pendingKey = _pendingOtherKey = 0;
        SaveRecordedChord(primary, _pendingModifiers, both);
    }

    private void SaveRecordedChord(int value, uint modifiers, bool both = false)
    {
        var mode = _recordingInput == ModeHotkeyInput;
        var status = mode ? ModeHotkeyStatus : HotkeyStatus;
        var parts = new List<string>();
        if ((modifiers & 2) != 0) parts.Add("Ctrl");
        if ((modifiers & 1) != 0) parts.Add("Alt");
        if ((modifiers & 4) != 0) parts.Add("Shift");
        if ((modifiers & 8) != 0) parts.Add("Win");
        parts.Add(both ? ",+." : value switch
        {
            0xBC => ",", 0xBE => ".", >= 0x70 and <= 0x87 => "F" + (value - 0x70 + 1), _ => ((char)value).ToString()
        });
        var chord = string.Join("+", parts);
        var otherEnabled = mode ? ViewModel.WeChatHotkeyEnabled : ViewModel.ModeSwitchHotkeyEnabled;
        var other = mode ? ViewModel.WeChatHotkey : ViewModel.ModeSwitchHotkey;
        if (otherEnabled && GlobalHotkeys.SameHotkey(chord, other))
        {
            status.Text = "微信热键与模式切换热键不能相同";
            return;
        }
        if ((mode || !WeChatWindow.IsRunning()) && !GlobalHotkeys.IsAvailable(_window.Handle, chord))
        {
            status.Text = "该快捷键已被其他程序占用";
            return;
        }
        if (mode) ViewModel.ModeSwitchHotkey = chord;
        else ViewModel.WeChatHotkey = chord;
        StopRecording();
    }

    private static bool Down(int key) => (Native.GetAsyncKeyState(key) & 0x8000) != 0;

    private async void OnBrowse(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _window.Handle);
            picker.FileTypeFilter.Add(".exe");
            var file = await picker.PickSingleFileAsync();
            if (file != null) ViewModel.WeChatPath = file.Path;
        }
        catch (Exception ex) { _window.ReportError("无法选择微信程序", ex.Message); }
    }

    private void OnDetect(object sender, RoutedEventArgs e)
    {
        try
        {
            ViewModel.WeChatPath = IconLoader.FindWeChatExePath()
                ?? throw new InvalidOperationException("未找到运行中的微信，请先启动微信或手动选择程序。");
        }
        catch (Exception ex) { _window.ReportError("无法识别微信路径", ex.Message); }
    }
}
