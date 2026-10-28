using Microsoft.UI.Xaml.Controls;
using WeChat_Notifier.ViewModels;

namespace WeChat_Notifier.Views.Pages;

public sealed partial class BehaviourPage : Page
{
    public SettingsViewModel ViewModel { get; }
    public BehaviourPage(SettingsViewModel viewModel) { ViewModel = viewModel; InitializeComponent(); }

    internal void CheckBindings()
    {
        var original = ViewModel.SkipMuted;
        SkipMutedToggle.IsOn = !original;
        if (ViewModel.SkipMuted == original) throw new InvalidOperationException("行为页开关的双向绑定失效。");
        SkipMutedToggle.IsOn = original;
        original = ViewModel.SuppressForActiveChat;
        ForegroundToggle.IsOn = !original;
        if (ViewModel.SuppressForActiveChat == original) throw new InvalidOperationException("微信前台抑制开关的双向绑定失效。");
        ForegroundToggle.IsOn = original;
    }
}
