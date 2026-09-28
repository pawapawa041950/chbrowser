using System.ComponentModel;
using System.Windows.Controls;
using ChBrowser.ViewModels;

namespace ChBrowser.Views.Settings;

/// <summary>「LLM」カテゴリのパネル (LLM プロファイルの登録・編集)。
/// API キーは PasswordBox (肩越し閲覧対策) で、PasswordBox.Password は直接バインドできないため、
/// 選択中のプロファイル (<see cref="SettingsViewModel.SelectedLlmProfile"/>) の <see cref="LlmProfileItem.ApiKey"/> と code-behind で同期する。</summary>
public partial class LlmPanel : UserControl
{
    private bool _suppressSync;

    public LlmPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SettingsViewModel oldVm) oldVm.PropertyChanged -= OnVmPropertyChanged;
            if (e.NewValue is SettingsViewModel newVm) newVm.PropertyChanged += OnVmPropertyChanged;
            SyncFromVm();
        };
        Loaded += (_, _) => SyncFromVm();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.SelectedLlmProfile)) SyncFromVm();
    }

    /// <summary>選択中のプロファイルの API キー → PasswordBox。</summary>
    private void SyncFromVm()
    {
        if (DataContext is not SettingsViewModel vm) return;
        _suppressSync = true;
        try
        {
            var key = vm.SelectedLlmProfile?.ApiKey ?? "";
            if (ApiKeyInput.Password != key) ApiKeyInput.Password = key;
        }
        finally { _suppressSync = false; }
    }

    private void ApiKeyInput_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_suppressSync) return;
        if (DataContext is SettingsViewModel { SelectedLlmProfile: { } item }) item.ApiKey = ApiKeyInput.Password;
    }
}
