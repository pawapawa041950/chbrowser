using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ChBrowser.ViewModels;

namespace ChBrowser.Views;

/// <summary>ショートカット & マウスジェスチャー設定ウィンドウ (Phase 15、現状はスタブ)。
/// メニュー「ツール → ショートカット & ジェスチャー...」から開く。モーダレス + シングルトン (App.xaml.cs 側で制御)。
/// 編集ボタンのクリックで <see cref="ShortcutEditDialog"/> / <see cref="GestureEditDialog"/> を開く。
/// 編集 OK が返ったら <see cref="ShortcutsWindowViewModel.MarkDirty"/> を呼んで未保存状態にする。
/// 「保存」で永続化、「閉じる」で(未保存があれば)確認プロンプト。</summary>
public partial class ShortcutsWindow : Window
{
    private ShortcutsWindowViewModel _vm;

    public ShortcutsWindow(ShortcutsWindowViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        _vm         = vm;
        Closing += ShortcutsWindow_Closing;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void SaveButton_Click(object sender, RoutedEventArgs e) => _vm.Save();

    /// <summary>未保存の変更があれば確認プロンプト。「保存」「破棄」「キャンセル」の 3 択。</summary>
    private void ShortcutsWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_vm.HasUnsavedChanges) return;
        var result = MessageBox.Show(
            this,
            "未保存の変更があります。保存しますか？",
            "確認",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        switch (result)
        {
            case MessageBoxResult.Yes:    _vm.Save(); break;
            case MessageBoxResult.No:                 break; // 破棄
            case MessageBoxResult.Cancel: e.Cancel = true; break;
        }
    }

    /// <summary>ショートカットセルの編集ボタン → <see cref="ShortcutEditDialog"/> を開く。
    /// OK が返ったら未保存フラグを立てる (スタブのため値の更新自体は省略)。</summary>
    private void EditShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ShortcutItem item }) return;
        var dlg = new ShortcutEditDialog(item.Action, item.Shortcut) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.NewBinding != item.Shortcut)
        {
            item.Shortcut = dlg.NewBinding;
            _vm.MarkDirty();
        }
    }

    /// <summary>ジェスチャーセルの編集ボタン → <see cref="GestureEditDialog"/> を開く。</summary>
    private void EditGesture_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ShortcutItem item }) return;
        var dlg = new GestureEditDialog(item.Action, item.Gesture) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.NewBinding != item.Gesture)
        {
            item.Gesture = dlg.NewBinding;
            _vm.MarkDirty();
        }
    }

    /// <summary>マウス操作セルの編集ボタン → <see cref="MouseEditDialog"/> を開く。</summary>
    private void EditMouse_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ShortcutItem item }) return;
        var dlg = new MouseEditDialog(item.Action, item.Mouse) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.NewBinding != item.Mouse)
        {
            item.Mouse = dlg.NewBinding;
            _vm.MarkDirty();
        }
    }
}
