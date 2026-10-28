using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;
using WeChat_Notifier.ViewModels;

namespace WeChat_Notifier.Views.Pages;

public sealed partial class BehaviourPage : Page
{
    private readonly MainWindow _window;
    private bool _picking;
    public SettingsViewModel ViewModel { get; }
    internal BehaviourPage(SettingsViewModel viewModel, MainWindow window)
    {
        ViewModel = viewModel;
        _window = window;
        InitializeComponent();
    }

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

    private async void OnAddSilentApplication(object sender, RoutedEventArgs e) => await AddApplicationAsync(silent: true);
    private async void OnAddPrivacyApplication(object sender, RoutedEventArgs e) => await AddApplicationAsync(silent: false);

    private async Task AddApplicationAsync(bool silent)
    {
        if (_picking) return;
        _picking = true;
        try
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _window.Handle);
            picker.FileTypeFilter.Add(".exe");
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            if (ViewModel.ContainsInOtherList(file.Path, silent))
            {
                var other = silent ? "隐私" : "静默";
                var target = silent ? "静默" : "隐私";
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "程序已在其他名单",
                    Content = $"{file.Name} 已存在{other}名单，是否移动到{target}名单？",
                    PrimaryButtonText = "移动",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            }
            ViewModel.AddApplication(file.Path, silent);
        }
        catch (Exception ex) { _window.ReportError("无法添加应用程序", ex.Message); }
        finally { _picking = false; }
    }

    private void OnRemoveSilentApplication(object sender, RoutedEventArgs e)
    {
        if (SilentApplicationsList.SelectedItem is not string path) return;
        ViewModel.RemoveApplication(path, silent: true);
    }

    private void OnRemovePrivacyApplication(object sender, RoutedEventArgs e)
    {
        if (PrivacyApplicationsList.SelectedItem is not string path) return;
        ViewModel.RemoveApplication(path, silent: false);
    }
}
