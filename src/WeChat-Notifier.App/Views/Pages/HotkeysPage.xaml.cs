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
    private bool _recording;
    public SettingsViewModel ViewModel { get; }

    internal HotkeysPage(SettingsViewModel viewModel, MainWindow window, GlobalHotkeys hotkeys)
    {
        ViewModel = viewModel;
        _window = window;
        _hotkeys = hotkeys;
        InitializeComponent();
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(500);
        _timer.Tick += (_, _) => HotkeyStatus.Text = _hotkeys.StatusText;
        Loaded += (_, _) => { HotkeyStatus.Text = _hotkeys.StatusText; _timer.Start(); };
    }

    private void OnHotkeyGotFocus(object sender, RoutedEventArgs e)
    {
        _recording = true;
        _hotkeys.SetRecording(true);
        HotkeyStatus.Text = "正在录制，原热键已临时释放";
    }

    private void StopRecording()
    {
        if (!_recording) return;
        _recording = false;
        _hotkeys.SetRecording(false);
    }

    private void OnHotkeyLostFocus(object sender, RoutedEventArgs e) => StopRecording();
    private void OnUnloaded(object sender, RoutedEventArgs e) { _timer.Stop(); StopRecording(); }

    private void OnHotkeyKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;
        if (e.Key == VirtualKey.Escape) { StopRecording(); return; }
        if (e.Key == VirtualKey.Back)
        {
            ViewModel.WeChatHotkeyEnabled = false;
            ViewModel.WeChatHotkey = "";
            StopRecording();
            return;
        }
        var value = (int)e.Key;
        if (value is not (>= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x70 and <= 0x87)) return;
        var parts = new List<string>();
        if (Down(0x11)) parts.Add("Ctrl");
        if (Down(0x12)) parts.Add("Alt");
        if (Down(0x10)) parts.Add("Shift");
        if (Down(0x5B) || Down(0x5C)) parts.Add("Win");
        if (parts.Count == 0) { HotkeyStatus.Text = "请至少按住一个修饰键"; return; }
        parts.Add(value >= 0x70 ? "F" + (value - 0x70 + 1) : ((char)value).ToString());
        var chord = string.Join("+", parts);
        if (!WeChatWindow.IsRunning() && !GlobalHotkeys.IsAvailable(_window.Handle, chord))
        {
            HotkeyStatus.Text = "该快捷键已被其他程序占用";
            return;
        }
        ViewModel.WeChatHotkey = chord;
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
