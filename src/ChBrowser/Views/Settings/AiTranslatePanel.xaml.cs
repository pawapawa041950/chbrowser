using System.ComponentModel;
using System.Windows.Controls;
using ChBrowser.ViewModels;

namespace ChBrowser.Views.Settings;

/// <summary>「AI翻訳」カテゴリのパネル (翻訳に使う AI モデル)。
///
/// API キーは <see cref="AiPanel"/> と同じく PasswordBox を使い、VM の
/// <see cref="SettingsViewModel.TranslateApiKey"/> と code-behind で双方向同期する。</summary>
public partial class AiTranslatePanel : UserControl
{
    private bool _suppressSync;

    public AiTranslatePanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => SyncFromVm();
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SettingsViewModel oldVm) oldVm.PropertyChanged -= OnVmPropertyChanged;
        if (e.NewValue is SettingsViewModel newVm) newVm.PropertyChanged += OnVmPropertyChanged;
        SyncFromVm();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.TranslateApiKey)) SyncFromVm();
    }

    private void SyncFromVm()
    {
        if (DataContext is not SettingsViewModel vm) return;
        _suppressSync = true;
        try
        {
            if (TranslateApiKeyInput.Password != (vm.TranslateApiKey ?? "")) TranslateApiKeyInput.Password = vm.TranslateApiKey ?? "";
        }
        finally { _suppressSync = false; }
    }

    private void TranslateApiKeyInput_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_suppressSync) return;
        if (DataContext is not SettingsViewModel vm) return;
        vm.TranslateApiKey = TranslateApiKeyInput.Password;
    }
}
