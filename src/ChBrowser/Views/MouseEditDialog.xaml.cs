using System.Windows;
using ChBrowser.Services.Shortcuts;

namespace ChBrowser.Views;

/// <summary>マウス操作の設定ダイアログ (Phase 15)。<see cref="MouseOperationCatalog"/> をドロップダウンで選択する形式。
/// 修飾なしの左/右クリックは選択肢に含めず (= 通常 UI 操作と衝突するため意図的に除外)。
/// OK で <see cref="DialogResult"/>=true、<see cref="NewBinding"/> に選択値が入る (空文字 = 未設定)。</summary>
public partial class MouseEditDialog : Window
{
    public string ActionName { get; }
    public string NewBinding { get; private set; }

    public MouseEditDialog(string actionName, string currentBinding)
    {
        InitializeComponent();
        ActionName = actionName;
        NewBinding = currentBinding;

        OperationCombo.ItemsSource   = MouseOperationCatalog.Options;
        OperationCombo.SelectedValue = currentBinding ?? "";
        // 既存値がカタログに無い場合は先頭 (= 未設定) にフォールバック
        if (OperationCombo.SelectedItem is null) OperationCombo.SelectedIndex = 0;

        CurrentBindingRun.Text = string.IsNullOrEmpty(currentBinding) ? "(未設定)" : currentBinding;
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
        => OperationCombo.SelectedIndex = 0; // (未設定)

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        NewBinding   = (OperationCombo.SelectedValue as string) ?? "";
        DialogResult = true;
    }
}
