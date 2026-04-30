using System.Windows;
using System.Windows.Controls;
using ChBrowser.ViewModels;

namespace ChBrowser.Views;

/// <summary>NG 設定ウィンドウ (Phase 13)。リサイズ可、シングルトン、モードレス。</summary>
public partial class NgWindow : Window
{
    private readonly NgWindowViewModel _vm;

    public NgWindow(NgWindowViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        _vm         = vm;
        Closed += (_, _) => _vm.NotifyClosed();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>有効化トグルのクリックで呼ばれる。トグルが ON になった直後に正規表現を検証し、
    /// 不正なら警告 + 強制 OFF。CheckBox の Click は IsChecked が既に切り替わった状態で発火する。</summary>
    private void EnableCheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.DataContext is not NgRuleViewModel rule) return;
        if (rule.Enabled)
        {
            // 有効化された直後 → 正規表現バリデーション
            _vm.ValidateBeforeEnable(rule);
        }
    }

    /// <summary>板名ボタンのクリックで <see cref="BoardPickerWindow"/> をモーダルで開き、
    /// OK で確定された scope を rule.SelectedScope にセットする。キャンセルなら何もしない。</summary>
    private void ScopeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.DataContext is not NgRuleViewModel rule) return;

        var picker = new BoardPickerWindow(_vm.AvailableScopes, rule.SelectedScope) { Owner = this };
        if (picker.ShowDialog() == true && picker.PickedScope is not null)
        {
            rule.SelectedScope = picker.PickedScope;
        }
    }
}
