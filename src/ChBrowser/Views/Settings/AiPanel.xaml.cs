using System.ComponentModel;
using System.Windows.Controls;
using ChBrowser.ViewModels;

namespace ChBrowser.Views.Settings;

/// <summary>「AI」カテゴリのパネル (LLM 連携設定)。
///
/// API キーは <see cref="AuthPanel"/> のどんぐりパスワードと同様、PasswordBox を使う
/// (= 肩越し閲覧対策)。PasswordBox.Password は WPF の制限で直接バインドできないため、
/// VM の <see cref="SettingsViewModel.LlmApiKey"/> との双方向同期を code-behind で取る:
/// <list type="bullet">
/// <item>ロード時 / VM 側変化時: VM → PasswordBox に流し込む</item>
/// <item>PasswordChanged: PasswordBox → VM に書き戻す</item>
/// </list>
/// 値の保管自体は config.json に平文で入る (パネル XAML に注意書きあり)。</summary>
public partial class AiPanel : UserControl
{
    private bool _suppressSync;

    public AiPanel()
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
        // VM の LlmApiKey が他経路 (= 初期ロード等) で変化したら PasswordBox に反映する。
        if (e.PropertyName == nameof(SettingsViewModel.LlmApiKey)) SyncFromVm();
    }

    /// <summary>VM → PasswordBox へ流し込む。<see cref="_suppressSync"/> で循環防止。</summary>
    private void SyncFromVm()
    {
        if (DataContext is not SettingsViewModel vm) return;
        if (ApiKeyInput.Password == vm.LlmApiKey) return;
        _suppressSync = true;
        try   { ApiKeyInput.Password = vm.LlmApiKey ?? ""; }
        finally { _suppressSync = false; }
    }

    private void ApiKeyInput_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_suppressSync) return;
        if (DataContext is not SettingsViewModel vm) return;
        vm.LlmApiKey = ApiKeyInput.Password;
    }
}
