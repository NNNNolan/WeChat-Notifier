using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml;
using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Graphics;
using Windows.Storage;
using WeChat_Notifier.Application;
using WeChat_Notifier.Infrastructure.Logging;
using WeChat_Notifier.Infrastructure.Windows;
using WeChat_Notifier.ViewModels;
using WeChat_Notifier.Views.Notifications;
using WeChat_Notifier.Views.Pages;

namespace WeChat_Notifier.Views;

/// <summary>WinUI 设置窗口；五页共用一个隔离的工作副本，编译绑定不依赖运行时反射。</summary>
public sealed partial class MainWindow : Window
{
    private readonly ApplicationController _controller;
    private readonly SettingsViewModel _viewModel;
    private AppearancePage? _appearance;
    private BehaviourPage? _behaviour;
    private HotkeysPage? _hotkeys;
    private AdvancedPage? _advanced;
    private AboutPage? _about;
    private bool _allowClose, _dialogOpen;
    public nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    internal MainWindow(ApplicationController controller)
    {
        _controller = controller;
        _viewModel = new SettingsViewModel(controller.Config);
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        var target = ScreenHelper.Resolve("Primary");
        var width = (int)Math.Round(1040 * target.Scale);
        var height = (int)Math.Round(800 * target.Scale);
        var work = target.WorkPhysical;
        AppWindow.Resize(new SizeInt32(width, height));
        AppWindow.Move(new PointInt32(work.Left + (work.Width - width) / 2, work.Top + (work.Height - height) / 2));
        AppWindow.Closing += OnClosing;
        _viewModel.PropertyChanged += OnOptionChanged;
        _controller.StatusChanged += RefreshStatus;
        Navigation.SelectedItem = Navigation.MenuItems[0];
        RefreshStatus();
        RefreshDirty();
        Closed += (_, _) =>
        {
            _viewModel.PropertyChanged -= OnOptionChanged;
            _controller.StatusChanged -= RefreshStatus;
        };
    }

    private void OnNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag } || PageHost == null) return;
        PageHost.Content = tag switch
        {
            "appearance" => _appearance ??= new AppearancePage(_viewModel),
            "behaviour" => _behaviour ??= new BehaviourPage(_viewModel),
            "hotkeys" => _hotkeys ??= new HotkeysPage(_viewModel, this, _controller.Hotkeys),
            "advanced" => _advanced ??= new AdvancedPage(_viewModel, this),
            "about" => _about ??= new AboutPage(_viewModel, this),
            _ => throw new InvalidOperationException("未知设置页：" + tag)
        };
        PageTitle.Text = tag switch
        {
            "appearance" => "外观",
            "behaviour" => "行为",
            "hotkeys" => "快捷键",
            "advanced" => "高级",
            _ => "关于"
        };
        PageDescription.Text = tag switch
        {
            "appearance" => "选择通知方式，调整气泡布局与玻璃材质。",
            "behaviour" => "只接收你关心的新消息，避免不必要的打扰。",
            "hotkeys" => "微信未运行时启动，运行后交还微信管理。",
            "advanced" => "调整读取频率和玻璃参数，查看本地诊断日志。",
            _ => "WinUI 原生界面，本地读取。"
        };
    }

    private void OnOptionChanged(object? sender, PropertyChangedEventArgs args)
    {
        RefreshDirty();
        if (_viewModel.UseWindowsNotifications)
        {
            _controller.EndPreview();
            return;
        }
        if (!string.IsNullOrEmpty(args.PropertyName) && !SettingsViewModel.AppearanceOptions.Contains(args.PropertyName)) return;
        try { _controller.PreviewAppearance(_viewModel.Model); }
        catch (Exception ex) { ReportError("无法更新气泡预览", ex.Message); }
    }

    private void RefreshDirty()
    {
        SaveButton.IsEnabled = _viewModel.IsDirty;
        DirtyText.Text = _viewModel.IsDirty ? $"{_viewModel.DirtyCount} 项修改未保存" : "所有修改已保存";
    }

    private void RefreshStatus()
    {
        ConnectionText.Text = _controller.Paused ? "通知已暂停" : _controller.ConnectionText + "\n" + _controller.WeChatStateText;
        PauseButton.Content = _controller.Paused ? "恢复通知" : "暂停通知";
    }

    private void OnPause(object sender, RoutedEventArgs e) => _controller.SetPaused(!_controller.Paused);
    private void OnPreview(object sender, RoutedEventArgs e)
    {
        try { _controller.Preview(_viewModel.Model); }
        catch (Exception ex) { ReportError("无法预览通知", ex.Message); }
    }

    private async void OnReset(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "恢复默认设置？",
                Content = "只更改当前工作副本。点击保存后才会写入配置文件。",
                PrimaryButtonText = "恢复默认",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) _viewModel.ResetToDefaults();
        }
        catch (Exception ex) { ReportError("无法恢复默认设置", ex.Message); }
        finally { _dialogOpen = false; }
    }

    private void OnSave(object sender, RoutedEventArgs e) => Save();
    private void OnSaveAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Save();
    }

    private void Save()
    {
        if (!_viewModel.IsDirty) return;
        try
        {
            _viewModel.Commit();
            _controller.Save(_viewModel.Model);
            _viewModel.AcceptChanges();
            Feedback.Title = "设置已保存";
            Feedback.Message = "新设置已立即应用，无需重启。";
            Feedback.Severity = InfoBarSeverity.Success;
            Feedback.IsOpen = true;
        }
        catch (Exception ex) { ReportError("无法保存设置", ex.Message); }
    }

    public void ReportError(string title, string message)
    {
        Log.Error(title + "：" + message);
        Feedback.Title = title;
        Feedback.Message = message;
        Feedback.Severity = InfoBarSeverity.Error;
        Feedback.IsOpen = true;
    }

    private async void OnClose(object sender, RoutedEventArgs e) => await RequestCloseAsync();
    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        await RequestCloseAsync();
    }

    private async Task RequestCloseAsync()
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            if (_controller.Config.ShowCloseToTrayHint)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = Root.XamlRoot,
                    RequestedTheme = Root.RequestedTheme,
                    Title = "程序将隐藏到托盘",
                    Content = "关闭窗口不会退出程序，消息通知会继续运行。\n需要退出时，请在托盘图标上右键选择「退出」。"
                        + (_viewModel.IsDirty ? "\n\n未保存的修改会保留，下次打开窗口可继续编辑。" : ""),
                    PrimaryButtonText = "不再提示",
                    SecondaryButtonText = "下次提示",
                    DefaultButton = ContentDialogButton.Secondary
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    _controller.DisableCloseToTrayHint();
                    _viewModel.SynchronizeCloseToTrayHint(false);
                }
            }
            _controller.EndPreview();
            AppWindow.Hide();
        }
        catch (Exception ex) { ReportError("无法关闭设置", ex.Message); }
        finally { _dialogOpen = false; }
    }

    internal void CloseForExit() { _allowClose = true; Close(); }

    internal async Task CapturePagesAsync(string directory, bool verify)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        await Task.Delay(600);
        var names = new[] { "1-appearance", "2-behaviour", "3-hotkeys", "4-advanced", "5-about" };
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            Root.RequestedTheme = theme;
            for (var i = 0; i < names.Length; i++)
            {
                Navigation.SelectedItem = Navigation.MenuItems[i];
                await Task.Delay(180);
                if (verify)
                {
                    switch (PageHost.Content)
                    {
                        case AppearancePage appearance:
                            appearance.CheckBindings();
                            _viewModel.UseWindowsNotifications = true;
                            appearance.CheckBindings();
                            _viewModel.UseWindowsNotifications = false;
                            appearance.CheckBindings();
                            break;
                        case BehaviourPage behaviour: behaviour.CheckBindings(); break;
                        case AdvancedPage advanced: advanced.CheckBindings(); break;
                    }
                    if (_viewModel.IsDirty) throw new InvalidOperationException("界面绑定初始化或还原操作修改了配置。");
                }
                var bitmap = new RenderTargetBitmap();
                await bitmap.RenderAsync(Root);
                if (bitmap.PixelWidth < 100 || bitmap.PixelHeight < 100)
                    throw new InvalidOperationException("WinUI 未生成有效设置页图像。");
                var pixels = (await bitmap.GetPixelsAsync()).ToArray();
                var file = await StorageFile.GetFileFromPathAsync(CreateFile(Path.Combine(directory, names[i] + "-" + theme + ".png")));
                using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
                await encoder.FlushAsync();
            }
        }
        _controller.EndPreview();
        Log.Info("WinUI pages rendered and bindings verified: " + directory);
    }

    private static string CreateFile(string path) { using var file = File.Create(path); return path; }
}
