using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using WeChat_Notifier.Configuration;
using WeChat_Notifier.Infrastructure.Windows;
using WeChat_Notifier.ViewModels;
using WeChat_Notifier.Views;

namespace WeChat_Notifier.Views.Pages;

public sealed partial class AdvancedPage : Page
{
    private readonly MainWindow _window;
    public SettingsViewModel ViewModel { get; }
    internal AdvancedPage(SettingsViewModel viewModel, MainWindow window) { ViewModel = viewModel; _window = window; InitializeComponent(); }
    private void OnOpenConfig(object sender, RoutedEventArgs e) => Open(AppPaths.ConfigFile, ShellOpen.File);
    private void OnOpenLog(object sender, RoutedEventArgs e) => Open(AppPaths.LogDirectory, ShellOpen.Folder);
    private void Open(string path, Action<string> open)
    {
        try { open(path); }
        catch (Exception ex) { _window.ReportError("无法打开路径", ex.Message); }
    }
    internal void CheckBindings()
    {
        if (PollSlider.Value != ViewModel.PollIntervalMs) throw new InvalidOperationException("高级页编译绑定失效。");
    }
}
