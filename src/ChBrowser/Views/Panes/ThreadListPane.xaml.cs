using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ChBrowser.Models;
using ChBrowser.Services.WebView2;
using ChBrowser.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace ChBrowser.Views.Panes;

/// <summary>スレッド一覧ペイン (Phase 23 抽出)。
/// ヘッダ + 新規スレ立てボタン + タブストリップ + 全 ThreadListTabViewModel の WebView2 を ItemsControl で並列保持。
/// 旧 MainWindow の ThreadListPane の役割を全て持ち込む。</summary>
public partial class ThreadListPane : UserControl
{
    public ThreadListPane()
    {
        InitializeComponent();
        ChBrowser.Controls.PaneDragInitiator.Attach(HeaderBar, ChBrowser.Models.PaneId.ThreadList);
    }

    // ---- ペインフォーカス → ViewModel に通知 ----

    private void Pane_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => (DataContext as MainViewModel)?.MarkThreadListPaneActive();

    private void Pane_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => (DataContext as MainViewModel)?.MarkThreadListPaneActive();

    // ---- ヘッダのボタン ----

    private void NewThreadButton_Click(object sender, RoutedEventArgs e)
    {
        (DataContext as MainViewModel)?.OpenNewThreadDialog();
    }

    /// <summary>選択中のスレ一覧タブを再取得する。タブ種別 (板 / 全ログ / お気に入り) を問わず
    /// MainViewModel.RefreshThreadListTabAsync が適切に振り分ける。タブ未選択なら no-op。</summary>
    private void RefreshThreadListButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        if (main.SelectedThreadListTab is null) return;
        _ = main.RefreshThreadListTabAsync(main.SelectedThreadListTab);
    }

    /// <summary>選択中の板タブの板をお気に入りに追加 / 削除する (トグル)。
    /// 板タブ以外 (お気に入り展開タブ / 全ログタブ) では XAML 側で IsEnabled=False になっているため呼ばれない。</summary>
    private void ToggleBoardFavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        if (main.SelectedThreadListTab?.Board is not { } board) return;
        main.ToggleBoardFavorite(board);
    }

    // ---- WebView2 → JS メッセージ受信 ----

    private async void ThreadListWebView_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        var (type, payload) = WebMessageBridge.TryParseMessage(e);

        if (type == "paneActivated") { main.MarkThreadListPaneActive(); return; }
        if (WebMessageBridge.TryDispatchCommonMessage(sender, type, payload, "スレ一覧表示領域")) return;

        if (type == "threadListRowMenu") { ShowThreadListRowContextMenu(payload); return; }
        if (type != "openThread") return;

        var host  = payload.TryGetProperty("host",          out var hp) ? hp.GetString() : null;
        var dir   = payload.TryGetProperty("directoryName", out var dp) ? dp.GetString() : null;
        var key   = payload.TryGetProperty("key",           out var kp) ? kp.GetString() : null;
        var title = payload.TryGetProperty("title",         out var tp) ? tp.GetString() : null;

        LogMarkState? hint = null;
        if (payload.TryGetProperty("logState", out var lp) && lp.TryGetInt32(out var li)
            && Enum.IsDefined(typeof(LogMarkState), li))
        {
            hint = (LogMarkState)li;
        }

        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(key)) return;
        await main.OpenThreadFromListAsync(host, dir, key, title ?? "", hint);
    }

    // ---- スレ一覧の各行の右クリックメニュー ----

    /// <summary>JS から飛んできた行 right-click 通知を受けて、WPF 側で <see cref="ContextMenu"/> を popup する。
    /// メニュー項目クリックハンドラには <see cref="ThreadListRowContext"/> を DataContext として伝える
    /// (= タブを開かずに「お気に入り追加 / コピー / 次スレ候補検索 / ログ削除」を実行可能)。</summary>
    private void ShowThreadListRowContextMenu(System.Text.Json.JsonElement payload)
    {
        if (DataContext is not MainViewModel main) return;
        var host  = payload.TryGetProperty("host",          out var hp) ? hp.GetString() : null;
        var dir   = payload.TryGetProperty("directoryName", out var dp) ? dp.GetString() : null;
        var key   = payload.TryGetProperty("key",           out var kp) ? kp.GetString() : null;
        var title = payload.TryGetProperty("title",         out var tp) ? tp.GetString() : null;
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(key)) return;

        var board = main.ResolveBoard(host, dir, "");
        var ctx   = new ThreadListRowContext(board, key, title ?? "");

        if (TryFindResource("ThreadListRowContextMenu") is not ContextMenu menu) return;
        menu.PlacementTarget = this;
        menu.Placement       = PlacementMode.MousePoint;
        menu.DataContext     = ctx;
        menu.IsOpen          = true;
    }

    /// <summary>右クリックメニューを開く瞬間に Header 文字列を「追加/削除」で動的に切替える。</summary>
    private void ThreadListRowContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm) return;
        if (cm.DataContext is not ThreadListRowContext ctx) return;
        if (DataContext is not MainViewModel main) return;

        var isFav = main.Favorites.FindThread(ctx.Board.Host, ctx.Board.DirectoryName, ctx.ThreadKey) is not null;
        foreach (var item in TabClickHelper.EnumerateAllMenuItems(cm))
        {
            if ((item.Tag as string) == "fav")
                item.Header = isFav ? "お気に入りから削除" : "お気に入りに追加";
        }
    }

    private static ThreadListRowContext? CtxOf(object sender)
        => (sender as MenuItem)?.DataContext as ThreadListRowContext;

    private void ThreadListRowFav_Click(object sender, RoutedEventArgs e)
    {
        if (CtxOf(sender) is not { } ctx) return;
        (DataContext as MainViewModel)?.ToggleThreadFavorite(ctx.Board, ctx.ThreadKey, ctx.Title);
    }

    private void ThreadListRowCopyTitle_Click(object sender, RoutedEventArgs e)
    {
        if (CtxOf(sender) is not { } ctx) return;
        if (!string.IsNullOrEmpty(ctx.Title)) Clipboard.SetText(ctx.Title);
    }

    private void ThreadListRowCopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if (CtxOf(sender) is not { } ctx) return;
        Clipboard.SetText(ThreadUrl(ctx));
    }

    private void ThreadListRowCopyTitleAndUrl_Click(object sender, RoutedEventArgs e)
    {
        if (CtxOf(sender) is not { } ctx) return;
        Clipboard.SetText($"{ctx.Title}\n{ThreadUrl(ctx)}");
    }

    private void ThreadListRowFindNext_Click(object sender, RoutedEventArgs e)
    {
        if (CtxOf(sender) is not { } ctx) return;
        if (DataContext is not MainViewModel main) return;
        _ = main.OpenNextThreadSearchAsync(ctx.Board, ctx.Title, ctx.ThreadKey);
    }

    private void ThreadListRowDeleteLog_Click(object sender, RoutedEventArgs e)
    {
        if (CtxOf(sender) is not { } ctx) return;
        (DataContext as MainViewModel)?.DeleteThreadLog(ctx.Board, ctx.ThreadKey, ctx.Title);
    }

    /// <summary>{board, key} → 5ch.io 系の read.cgi 形式 URL を組み立てる
    /// (= <see cref="ThreadTabViewModel.Url"/> と同じ形式で揃える)。</summary>
    private static string ThreadUrl(ThreadListRowContext ctx)
        => $"https://{ctx.Board.Host}/test/read.cgi/{ctx.Board.DirectoryName}/{ctx.ThreadKey}/";

    /// <summary>スレ一覧行の右クリックメニュー操作で必要な値を 1 つに束ねた immutable record。
    /// Board は <see cref="MainViewModel.ResolveBoard"/> で解決済み (= bbsmenu 未登録の板でも fallback Board が入る)。</summary>
    private sealed record ThreadListRowContext(Board Board, string ThreadKey, string Title);

    // ---- タブの右クリックメニュー (中/ダブル/修飾+左 は ShortcutManager 側で dispatch) ----

    private void ThreadListTabItem_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TabItem ti) return;
        if (ti.DataContext is null) return;
        if (e.OriginalSource is DependencyObject src && TabClickHelper.FindAncestor<ButtonBase>(src) is not null) return;
        if (TryFindResource("ThreadListTabContextMenu") is not ContextMenu menu) return;

        menu.PlacementTarget = ti;
        menu.Placement       = PlacementMode.MousePoint;
        menu.DataContext     = ti.DataContext;
        menu.IsOpen          = true;
        e.Handled            = true;
    }

    // ---- 右クリックメニュー: 動的な Header / IsEnabled 切替 ----

    private void ThreadListTabContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm) return;
        if (cm.DataContext is not ThreadListTabViewModel tab) return;
        if (DataContext is not MainViewModel main) return;

        var hasBoard = tab.Board is not null;
        var isFav    = tab.Board is { } b && main.Favorites.FindBoard(b.Host, b.DirectoryName) is not null;

        foreach (var item in TabClickHelper.EnumerateAllMenuItems(cm))
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

    private static T? TabOf<T>(object sender) where T : class
        => (sender as MenuItem)?.DataContext as T;

    private void ThreadListTabFav_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf<ThreadListTabViewModel>(sender) is not { Board: { } board }) return;
        (DataContext as MainViewModel)?.ToggleBoardFavorite(board);
    }

    private void ThreadListTabCopyTitle_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf<ThreadListTabViewModel>(sender) is not { } tab) return;
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
}
