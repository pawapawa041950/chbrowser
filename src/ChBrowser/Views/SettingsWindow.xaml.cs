using System.Windows;
using ChBrowser.ViewModels;

namespace ChBrowser.Views;

/// <summary>
/// 設定ウィンドウ (Phase 11)。VS Code 風の左カテゴリ + 右ペイン構成。
/// 設定変更は debounce で即時反映 (= 「保存」ボタン無し)。詳細は §5.7。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        _vm         = vm;
        // ウィンドウを閉じるときに debounce で保留中の保存があれば確定させる
        Closed += (_, _) => _vm.FlushPendingSave();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
