using System.ComponentModel;
using System.Windows.Controls;
using ChBrowser.ViewModels;

namespace ChBrowser.Views.Settings;

/// <summary>「AI NG」カテゴリのパネル (NG 判定 AI = 攻撃的レスの自動非表示)。
///
/// API キーは <see cref="AiPanel"/> と同じく PasswordBox を使い、VM の
/// <see cref="SettingsViewModel.NgAiApiKey"/> と code-behind で双方向同期する。</summary>
public partial class AiNgPanel : UserControl
{
    private bool _suppressSync;

    public AiNgPanel()
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
        if (e.PropertyName == nameof(SettingsViewModel.NgAiApiKey)) SyncFromVm();
    }

    private void SyncFromVm()
    {
        if (DataContext is not SettingsViewModel vm) return;
        _suppressSync = true;
        try
        {
            if (NgAiApiKeyInput.Password != (vm.NgAiApiKey ?? "")) NgAiApiKeyInput.Password = vm.NgAiApiKey ?? "";
        }
        finally { _suppressSync = false; }
    }

    private void NgAiApiKeyInput_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_suppressSync) return;
        if (DataContext is not SettingsViewModel vm) return;
        vm.NgAiApiKey = NgAiApiKeyInput.Password;
    }
}
