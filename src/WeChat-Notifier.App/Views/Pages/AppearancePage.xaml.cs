using Microsoft.UI.Xaml.Controls;
using WeChat_Notifier.ViewModels;

namespace WeChat_Notifier.Views.Pages;

public sealed partial class AppearancePage : Page
{
    public SettingsViewModel ViewModel { get; }
    public AppearancePage(SettingsViewModel viewModel) { ViewModel = viewModel; InitializeComponent(); }

    internal void CheckBindings()
    {
        if (SystemNotificationsToggle.IsOn != ViewModel.UseWindowsNotifications || BubbleSettings.IsEnabled != ViewModel.UseCustomBubble)
            throw new InvalidOperationException("通知模式的 WinUI 编译绑定失效。");
        var original = ViewModel.Width;
        WidthSlider.Value = original + 2;
        if (ViewModel.Width != original + 2) throw new InvalidOperationException("气泡宽度的双向绑定失效。");
        WidthSlider.Value = original;
    }
}
