using System.ComponentModel;
using System.Windows.Controls;
using ChBrowser.ViewModels;

namespace ChBrowser.Views.Settings;

/// <summary>「認証」カテゴリのパネル (Phase: どんぐりメール認証)。
///
/// PasswordBox.Password は WPF の制限で直接バインドできない (SecureString が漏洩しないようにという設計)。
/// 仕方ないので双方向同期は code-behind で取る:
/// <list type="bullet">
/// <item>ロード時: VM の DonguriPassword を PasswordInput に流し込む</item>
/// <item>PasswordChanged: PasswordInput の値を VM に書き戻す</item>
/// </list>
/// 設定値の保管自体は config.json に平文で入る (パネル XAML に注意書きあり)。</summary>
public partial class AuthPanel : UserControl
{
    private bool _suppressSync;

    public AuthPanel()
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
        // VM の DonguriPassword が他経路 (= 初期ロード等) で変化したら PasswordBox に反映する。
        if (e.PropertyName == nameof(SettingsViewModel.DonguriPassword)) SyncFromVm();
    }

    /// <summary>VM → PasswordBox へ流し込む。<see cref="_suppressSync"/> で循環防止。</summary>
    private void SyncFromVm()
    {
        if (DataContext is not SettingsViewModel vm) return;
        if (PasswordInput.Password == vm.DonguriPassword) return;
        _suppressSync = true;
        try   { PasswordInput.Password = vm.DonguriPassword ?? ""; }
        finally { _suppressSync = false; }
    }

    private void PasswordInput_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_suppressSync) return;
        if (DataContext is not SettingsViewModel vm) return;
        vm.DonguriPassword = PasswordInput.Password;
    }
}
