using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ChBrowser.ViewModels;

namespace ChBrowser;

/// <summary>タブ系 / ペインフォーカス系のイベントハンドラ。
/// 修飾子付き左クリック / 中クリック / ダブルクリック等は <see cref="PickClickAction"/> で
/// 共通的に解析し、設定で割り当てたアクション ID を <see cref="MainViewModel.ExecuteThreadListTabAction"/> /
/// <see cref="MainViewModel.ExecuteThreadTabAction"/> に dispatch する。</summary>
public partial class MainWindow
{
    /// <summary>右ペイン上段「スレ欄」 (ThreadListTabs) がフォーカス取得 — VM に通知してアドレスバーを板 URL に切替。</summary>
    private void ThreadListPane_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => _vm?.MarkThreadListPaneActive();

    /// <summary>右ペイン下段「スレ表示」 (ThreadTabs) がフォーカス取得 — VM に通知してアドレスバーをスレ URL に切替。</summary>
    private void ThreadPane_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => _vm?.MarkThreadPaneActive();

    /// <summary>タブ見出しなど WebView2 外の要素クリックは tunnel フェーズで拾う。
    /// (上段 TabControl の見出しクリックは TabItem ではなく直接 WebView2 に focus が飛ぶ場合があり、
    /// GotKeyboardFocus の bubble が HwndHost 越しに上がってこないため、こちらの経路で確実に捕捉する)。</summary>
    private void ThreadListPane_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _vm?.MarkThreadListPaneActive();

    private void ThreadPane_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _vm?.MarkThreadPaneActive();

    /// <summary>修飾子付き左クリック / 中クリックを設定で割り当てたアクションに振り分ける共通処理。
    /// 子要素の Button (× 等) のクリックは除外する。</summary>
    private static string? PickClickAction(MouseButtonEventArgs e, string ctrlAction, string shiftAction, string altAction, string middleAction)
    {
        if (e.OriginalSource is DependencyObject src && FindAncestor<ButtonBase>(src) is not null)
            return null;

        if (e.ChangedButton == MouseButton.Middle && e.ButtonState == MouseButtonState.Pressed)
            return middleAction;

        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed && e.ClickCount == 1)
        {
            var mods = Keyboard.Modifiers;
            if      ((mods & ModifierKeys.Control) == ModifierKeys.Control) return ctrlAction;
            else if ((mods & ModifierKeys.Shift)   == ModifierKeys.Shift)   return shiftAction;
            else if ((mods & ModifierKeys.Alt)     == ModifierKeys.Alt)     return altAction;
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = VisualTreeHelper.GetParent(d);
        return d as T;
    }

    private void ThreadListTabItem_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TabItem ti || ti.DataContext is not ThreadListTabViewModel tab) return;
        if (DataContext is not MainViewModel main) return;
        var cfg = main.CurrentConfig;
        var action = PickClickAction(e,
            cfg.ThreadListTabCtrlClickAction,
            cfg.ThreadListTabShiftClickAction,
            cfg.ThreadListTabAltClickAction,
            cfg.ThreadListTabMiddleClickAction);
        if (action is null or "none") return;
        main.ExecuteThreadListTabAction(tab, action);
        e.Handled = true;
    }

    private void ThreadListTabItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TabItem ti || ti.DataContext is not ThreadListTabViewModel tab) return;
        if (DataContext is not MainViewModel main) return;
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is DependencyObject src && FindAncestor<ButtonBase>(src) is not null) return;
        var action = main.CurrentConfig.ThreadListTabDoubleClickAction;
        if (action is "" or "none") return;
        main.ExecuteThreadListTabAction(tab, action);
        e.Handled = true;
    }

    private void ThreadTabItem_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TabItem ti || ti.DataContext is not ThreadTabViewModel tab) return;
        if (DataContext is not MainViewModel main) return;
        var cfg = main.CurrentConfig;
        var action = PickClickAction(e,
            cfg.ThreadTabCtrlClickAction,
            cfg.ThreadTabShiftClickAction,
            cfg.ThreadTabAltClickAction,
            cfg.ThreadTabMiddleClickAction);
        if (action is null or "none") return;
        main.ExecuteThreadTabAction(tab, action);
        e.Handled = true;
    }

    private void ThreadTabItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TabItem ti || ti.DataContext is not ThreadTabViewModel tab) return;
        if (DataContext is not MainViewModel main) return;
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is DependencyObject src && FindAncestor<ButtonBase>(src) is not null) return;
        var action = main.CurrentConfig.ThreadTabDoubleClickAction;
        if (action is "" or "none") return;
        main.ExecuteThreadTabAction(tab, action);
        e.Handled = true;
    }

    /// <summary>スレ一覧タブ上のツールバー「新規スレ立て」ボタン。
    /// SelectedThreadListTab が板タブのときだけ有効化されている (XAML 側 DataTrigger)。</summary>
    private void NewThreadButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel main)
            main.OpenNewThreadDialog();
    }

    // -----------------------------------------------------------------
    // タブ右クリックメニュー (Phase 21)
    // -----------------------------------------------------------------
    //
    // ContextMenu は Window.Resources に <c>x:Shared="False"</c> で定義してあり、
    // 各タブの <c>MouseRightButtonUp</c> ハンドラが Resources から fresh instance を取り出して、
    // <c>PlacementTarget</c> + <c>DataContext</c> を明示的に積んでから <c>IsOpen=true</c> で popup する。
    // (Setter ベースの自動表示パターン + RelativeSource バインドだと popup の DataContext 継承が
    // 正しく伝わらず Click ハンドラが空振りする現象があったため、この explicit パターンに統一した。
    // 既存の Favorites / Board ContextMenu と同じ流儀。)
    //
    // Click ハンドラは <c>(sender as MenuItem).DataContext as T</c> で対象タブを取れる
    // (= ContextMenu に DataContext を積んだので、配下の MenuItem には標準の DataContext 継承で伝わる)。
    // 開いた瞬間 (Opened) に内容を動的調整する: お気に入り済み判定で header を切替、
    // Board=null のアグリゲートタブでは URL 系 / お気に入りを無効化。

    /// <summary>右クリック時にスレ一覧タブ用 ContextMenu を Resources から取り出して open する。</summary>
    private void ThreadListTabItem_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        => ShowTabContextMenu(sender, e, "ThreadListTabContextMenu");

    /// <summary>右クリック時にスレ表示タブ用 ContextMenu を Resources から取り出して open する。</summary>
    private void ThreadTabItem_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        => ShowTabContextMenu(sender, e, "ThreadTabContextMenu");

    /// <summary>TabItem 用 ContextMenu の共通 popup 処理。
    /// 子要素の Button (× 等) のクリックでは出さない。</summary>
    private void ShowTabContextMenu(object sender, MouseButtonEventArgs e, string resourceKey)
    {
        if (sender is not TabItem ti) return;
        if (ti.DataContext is null) return;
        // × 閉じるボタンの右クリックでは出さない
        if (e.OriginalSource is DependencyObject src && FindAncestor<ButtonBase>(src) is not null) return;
        if (Resources[resourceKey] is not ContextMenu menu) return;

        menu.PlacementTarget = ti;
        menu.Placement       = PlacementMode.MousePoint;
        menu.DataContext     = ti.DataContext; // ← 配下 MenuItem への DataContext 継承の起点
        menu.IsOpen          = true;
        e.Handled            = true;
    }

    /// <summary>クリックされた MenuItem の DataContext (= タブ ViewModel) を <typeparamref name="T"/> で取り出す。
    /// ContextMenu に DataContext を積んでいるため、入れ子 MenuItem (= コピー サブメニュー) でも継承で同じ ViewModel。</summary>
    private static T? TabOf<T>(object sender) where T : class
        => (sender as MenuItem)?.DataContext as T;

    /// <summary>ContextMenu の項目を再帰展開して全 MenuItem を列挙する (サブメニュー内の項目も含む)。</summary>
    private static IEnumerable<MenuItem> EnumerateAllMenuItems(ItemsControl root)
    {
        foreach (var item in root.Items)
        {
            if (item is not MenuItem mi) continue;
            yield return mi;
            foreach (var sub in EnumerateAllMenuItems(mi)) yield return sub;
        }
    }

    /// <summary>スレ一覧タブ ContextMenu.Opened: アグリゲートタブ (Board=null) では URL 系 / お気に入りを無効化、
    /// お気に入り済みなら header を「お気に入りから削除」に切替。</summary>
    private void ThreadListTabContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm) return;
        if (cm.DataContext is not ThreadListTabViewModel tab) return;
        if (DataContext is not MainViewModel main) return;

        var hasBoard = tab.Board is not null;
        var isFav    = tab.Board is { } b && main.Favorites.FindBoard(b.Host, b.DirectoryName) is not null;

        foreach (var item in EnumerateAllMenuItems(cm))
        {
            switch (item.Tag as string)
            {
                case "fav":
                    item.IsEnabled = hasBoard;
                    item.Header    = isFav ? "お気に入りから削除" : "お気に入りに追加";
                    break;
                case "copyUrl":
                    item.IsEnabled = hasBoard;
                    break;
            }
        }
    }

    /// <summary>スレ表示タブ ContextMenu.Opened: お気に入り header の動的切替のみ
    /// (Board は常に非 null なので IsEnabled の切替は不要)。</summary>
    private void ThreadTabContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm) return;
        if (cm.DataContext is not ThreadTabViewModel tab) return;

        foreach (var item in EnumerateAllMenuItems(cm))
        {
            if ((item.Tag as string) == "fav")
                item.Header = tab.IsFavorited ? "お気に入りから削除" : "お気に入りに追加";
        }
    }

    // ---- スレ一覧タブの右クリックメニュー ----

    private void ThreadListTabFav_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf<ThreadListTabViewModel>(sender) is not { Board: { } board }) return;
        if (DataContext is not MainViewModel main) return;
        main.ToggleBoardFavorite(board);
    }

    private void ThreadListTabCopyTitle_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf<ThreadListTabViewModel>(sender) is not { } tab) return;
        // 板タブなら板名、アグリゲートタブなら現在の Header (= "★ ..." / "📁 ..." 込み)
        var title = tab.Board?.BoardName ?? tab.Header ?? "";
        if (!string.IsNullOrEmpty(title)) Clipboard.SetText(title);
    }

    private void ThreadListTabCopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf<ThreadListTabViewModel>(sender) is not { Board: { } board }) return;
        Clipboard.SetText(board.Url);
    }

    private void ThreadListTabCopyTitleAndUrl_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf<ThreadListTabViewModel>(sender) is not { Board: { } board }) return;
        Clipboard.SetText($"{board.BoardName}\n{board.Url}");
    }

    // ---- スレ表示タブの右クリックメニュー ----

    private void ThreadTabFav_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf<ThreadTabViewModel>(sender) is not { } tab) return;
        if (DataContext is not MainViewModel main) return;
        main.ToggleThreadFavorite(tab);
    }

    private void ThreadTabCopyTitle_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf<ThreadTabViewModel>(sender) is not { } tab) return;
        Clipboard.SetText(tab.Title ?? "");
    }

    private void ThreadTabCopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf<ThreadTabViewModel>(sender) is not { } tab) return;
        Clipboard.SetText(tab.Url);
    }

    private void ThreadTabCopyTitleAndUrl_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf<ThreadTabViewModel>(sender) is not { } tab) return;
        Clipboard.SetText($"{tab.Title}\n{tab.Url}");
    }

    private void ThreadTabFindNext_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf<ThreadTabViewModel>(sender) is not { } tab) return;
        if (DataContext is not MainViewModel main) return;
        _ = main.OpenNextThreadSearchAsync(tab);
    }

    private void ThreadTabDeleteLog_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf<ThreadTabViewModel>(sender) is not { } tab) return;
        if (DataContext is not MainViewModel main) return;
        main.DeleteThreadLog(tab);
    }
}
