using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ChBrowser.Services.Shortcuts;
using ChBrowser.ViewModels;

namespace ChBrowser.Views;

/// <summary>ショートカット & マウスジェスチャー設定ウィンドウ。
/// メニュー「ツール → ショートカット & ジェスチャー...」から開く。モーダレス + シングルトン (App.xaml.cs 側で制御)。
/// 編集ボタンのクリックで <see cref="ShortcutEditDialog"/> / <see cref="MouseEditDialog"/> /
/// <see cref="GestureEditDialog"/> を開く。OK が返ったら衝突検査を通った場合に値を反映 +
/// <see cref="ShortcutsWindowViewModel.MarkDirty"/>。「保存」で永続化、「閉じる」で(未保存があれば)確認プロンプト。</summary>
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

    // -------------------------------------------------------------------------
    // 編集ボタン → 各 EditDialog → 衝突検査 → 反映 / 拒否 メッセージ
    //
    // 3 種 (キー / マウス / ジェスチャー) で完全に同じ流れなので、共通ヘルパ <see cref="EditBindingCell"/>
    // にまとめて、ダイアログ生成と「現在値 / 反映先」の getter/setter だけ呼出側が渡す形にする。
    // -------------------------------------------------------------------------

    /// <summary>ショートカット (キーボード) — アプリ全体で 1 つのアクションにしか割り当てられない。</summary>
    private void EditShortcut_Click(object sender, RoutedEventArgs e)
        => EditBindingCell(sender, BindingKind.Shortcut,
                           item => item.Shortcut,
                           (item, v) => item.Shortcut = v,
                           item => { var d = new ShortcutEditDialog(item.Action, item.Shortcut); return (d, () => d.NewBinding); });

    /// <summary>マウス操作 — 同じカテゴリ内で重複不可、加えて「全体」とペインカテゴリ間でも重複不可。
    /// (= 別ペイン同士で同じ操作を使うのは OK = スコープが分かれているため衝突しない)</summary>
    private void EditMouse_Click(object sender, RoutedEventArgs e)
        => EditBindingCell(sender, BindingKind.Mouse,
                           item => item.Mouse,
                           (item, v) => item.Mouse = v,
                           item => { var d = new MouseEditDialog(item.Action, item.Mouse);    return (d, () => d.NewBinding); });

    /// <summary>マウスジェスチャー — マウス操作と同じ衝突ルール。</summary>
    private void EditGesture_Click(object sender, RoutedEventArgs e)
        => EditBindingCell(sender, BindingKind.Gesture,
                           item => item.Gesture,
                           (item, v) => item.Gesture = v,
                           item => { var d = new GestureEditDialog(item.Action, item.Gesture); return (d, () => d.NewBinding); });

    /// <summary>3 種共通のセル編集フロー: sender→item キャスト → ダイアログ表示 → 衝突検査 → 反映。
    /// <paramref name="dialogFactory"/> はダイアログ本体と「OK 後の新値を取り出す Func」をペアで返す
    /// (各 EditDialog の <c>NewBinding</c> プロパティ型は共通だが基底型 / interface を切るほどではないため
    /// クロージャで吸収)。</summary>
    private void EditBindingCell(
        object sender,
        BindingKind kind,
        System.Func<ShortcutItem, string>                            getCurrent,
        System.Action<ShortcutItem, string>                          setNew,
        System.Func<ShortcutItem, (Window dialog, System.Func<string> getNew)> dialogFactory)
    {
        if (sender is not Button { DataContext: ShortcutItem item }) return;
        var (dlg, getNew) = dialogFactory(item);
        dlg.Owner = this;
        if (dlg.ShowDialog() != true) return;

        var newBinding = getNew();
        if (newBinding == getCurrent(item)) return;

        if (FindBindingConflict(item, newBinding, kind) is { } conflict)
        {
            ShowConflictMessage(conflict, kind);
            return;
        }
        setNew(item, newBinding);
        _vm.MarkDirty();
    }

    // -------------------------------------------------------------------------
    // 衝突検査本体
    // -------------------------------------------------------------------------

    private enum BindingKind { Shortcut, Mouse, Gesture }

    /// <summary>編集中アイテム <paramref name="editing"/> に <paramref name="newValue"/> を当てたとき、
    /// 同じ値で衝突する別アイテムを返す。空文字 (= 未設定への変更) は無条件で null。
    ///
    /// <list type="bullet">
    /// <item>Shortcut: アプリ全体で 1 つのアクションにしか割り当てられない (= 任意のカテゴリ間で衝突)</item>
    /// <item>Mouse / Gesture: 同カテゴリ内で衝突、加えて「全体 ↔ ペインカテゴリ」で跨ぐ場合にも衝突
    ///       (= 「全体」のバインドが他全カテゴリにマージされる仕様 — pane 側に同 descriptor があると pane が
    ///       勝って「全体」が黙って無効化される事象を防止)。pane 同士はカテゴリスコープで分離されるため衝突しない。</item>
    /// </list></summary>
    private ShortcutItem? FindBindingConflict(ShortcutItem editing, string newValue, BindingKind kind)
    {
        if (string.IsNullOrEmpty(newValue)) return null;
        var globalCat       = CategoryResolver.GlobalCategory;
        bool editingIsGlobal = editing.Category == globalCat;

        foreach (var other in _vm.Items)
        {
            if (ReferenceEquals(other, editing)) continue;
            string otherValue = kind switch
            {
                BindingKind.Shortcut => other.Shortcut,
                BindingKind.Mouse    => other.Mouse,
                BindingKind.Gesture  => other.Gesture,
                _                    => "",
            };
            if (otherValue != newValue) continue;

            if (kind == BindingKind.Shortcut)
                return other; // キーボードはどこのカテゴリ間でも衝突

            // Mouse / Gesture: 同カテゴリ OR 全体↔ペイン跨ぎ
            bool otherIsGlobal = other.Category == globalCat;
            bool sameCategory  = other.Category == editing.Category;
            if (sameCategory || (editingIsGlobal != otherIsGlobal)) return other;
        }
        return null;
    }

    private void ShowConflictMessage(ShortcutItem conflict, BindingKind kind)
    {
        (string label, string scope) = kind switch
        {
            BindingKind.Shortcut => ("ショートカット",
                                     "ショートカットはアプリ全体で 1 つのアクションにしか割り当てられません。"),
            BindingKind.Mouse    => ("マウス操作",
                                     "マウス操作は同じカテゴリ内、および「全体」とペインカテゴリの間で共有できません。"),
            BindingKind.Gesture  => ("マウスジェスチャー",
                                     "マウスジェスチャーは同じカテゴリ内、および「全体」とペインカテゴリの間で共有できません。"),
            _                    => ("バインド", ""),
        };
        MessageBox.Show(
            this,
            $"この{label}は「{conflict.Action}」({conflict.Category}) で既に使われています。\n{scope}",
            $"{label}が重複しています",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
