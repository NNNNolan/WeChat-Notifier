using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
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
    private void OnOpenConfig(object sender, RoutedEventArgs e) => Open(AppPaths.ConfigFile);
    private void OnOpenLog(object sender, RoutedEventArgs e) => Open(AppPaths.LogDirectory);
    private void Open(string path)
    {
        try { ShellOpen.File(path); }
        catch (Exception ex) { _window.ReportError("无法打开文件", ex.Message); }
    }
}
