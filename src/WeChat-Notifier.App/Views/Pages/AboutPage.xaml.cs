using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.CompilerServices;
using WeChat_Notifier.Configuration;
using WeChat_Notifier.Infrastructure.Windows.Interop;
using WeChat_Notifier.Infrastructure.Windows;
using WeChat_Notifier.ViewModels;
using WeChat_Notifier.Views;

namespace WeChat_Notifier.Views.Pages;

public sealed partial class AboutPage : Page
{
    private readonly MainWindow _window;
    public SettingsViewModel ViewModel { get; }
    internal AboutPage(SettingsViewModel viewModel, MainWindow window)
    {
        ViewModel = viewModel;
        _window = window;
        InitializeComponent();
        BuildInfo.Text = $"v{ViewModel.Version} · WinUI 3 · {(RuntimeFeature.IsDynamicCodeSupported ? ".NET" : "Native AOT")}";
    }
    private void OnOpenConfig(object sender, RoutedEventArgs e) => Open(AppPaths.ConfigFile, ShellOpen.File);
    private void OnOpenLog(object sender, RoutedEventArgs e) => Open(AppPaths.LogDirectory, ShellOpen.Folder);
    private void Open(string path, Action<string> open)
    {
        try { open(path); }
        catch (Exception ex) { _window.ReportError("无法打开路径", ex.Message); }
    }

    internal void CheckIcon()
    {
        if (ProductIcon.Source is not BitmapImage { PixelWidth: > 0, PixelHeight: > 0 })
            throw new InvalidOperationException("关于页产品图标没有成功加载。");
    }
}
