using System.Collections.Generic;

namespace ChBrowser.Services.Shortcuts;

/// <summary>ショートカット & マウスジェスチャーで設定可能な 1 アクション (Phase 15)。
/// <see cref="Id"/> は内部識別子 (スネークケース、ドット階層)。<see cref="DisplayName"/> は UI 表示用。
/// <see cref="DefaultShortcut"/> / <see cref="DefaultGesture"/> はユーザが <c>shortcuts.json</c> でこの Id を
/// オーバーライドしていないときに使われる。空文字 = 既定では未割り当て。</summary>
public sealed record ShortcutAction(
    string Id,
    string Category,
    string DisplayName,
    string DefaultShortcut,
    string DefaultMouse,
    string DefaultGesture);

/// <summary>ChBrowser で扱うショートカット可能なアクションの全集合 (Phase 15)。
/// 既定値はユーザの依頼により「現状のハードコードを反映」した最小構成 — つまりほぼ空。
/// 例外として <c>main.focus_address_bar</c> のみ Phase 14 で <c>Ctrl+L</c> に固定されていたため、
/// 既定値として残す (= 互換性維持)。
/// 後続フェーズでユーザがプリセットを送ってきたら、この既定表を更新する。</summary>
public static class ShortcutRegistry
{
    public static IReadOnlyList<ShortcutAction> Actions { get; } = new[]
    {
        // メインウィンドウ
        new ShortcutAction("main.focus_address_bar",  "メインウィンドウ", "アドレスバーへフォーカス", "Ctrl+L", "", ""),
        new ShortcutAction("main.refresh_board_list", "メインウィンドウ", "板一覧を更新",             "",       "", ""),
        new ShortcutAction("main.exit",               "メインウィンドウ", "終了",                     "",       "", ""),
        new ShortcutAction("main.history_back",       "メインウィンドウ", "戻る (履歴)",              "",       "", ""),
        new ShortcutAction("main.history_forward",    "メインウィンドウ", "進む (履歴)",              "",       "", ""),

        // スレ一覧表示領域 (= 板のスレ一覧テーブル)
        new ShortcutAction("thread_list.refresh", "スレ一覧表示領域", "選択中の板を更新", "", "", ""),

        // スレ一覧のタブ領域 (= 板タブのストリップ)
        new ShortcutAction("thread_list.close_current", "スレ一覧のタブ領域", "現在のスレ一覧タブを閉じる", "", "", ""),
        new ShortcutAction("thread_list.next_tab",      "スレ一覧のタブ領域", "次のタブ",                   "", "", ""),
        new ShortcutAction("thread_list.prev_tab",      "スレ一覧のタブ領域", "前のタブ",                   "", "", ""),

        // スレッド表示領域 (= レス本文ビュー)
        new ShortcutAction("thread.refresh",        "スレッド表示領域", "現在のスレを更新", "", "", ""),
        new ShortcutAction("thread.new_thread",     "スレッド表示領域", "新規スレッド作成", "", "", ""),
        new ShortcutAction("thread.search_in_page", "スレッド表示領域", "ページ内検索",     "", "", ""),

        // スレッドタブ表示領域 (= スレタブのストリップ)
        new ShortcutAction("thread.close_current",  "スレッドタブ表示領域", "現在のタブを閉じる", "", "", ""),
        new ShortcutAction("thread.next_tab",       "スレッドタブ表示領域", "次のタブ",           "", "", ""),
        new ShortcutAction("thread.prev_tab",       "スレッドタブ表示領域", "前のタブ",           "", "", ""),

        // お気に入りペイン
        new ShortcutAction("favorites.new_folder", "お気に入りペイン", "新規フォルダ", "", "", ""),
        new ShortcutAction("favorites.rename",     "お気に入りペイン", "名前変更",     "", "", ""),
        new ShortcutAction("favorites.delete",     "お気に入りペイン", "削除",         "", "", ""),

        // 板一覧ペイン
        new ShortcutAction("board_list.search_in_page", "板一覧ペイン", "ページ内検索", "", "", ""),

        // ビューアウィンドウ
        new ShortcutAction("viewer.next_image", "ビューアウィンドウ", "次の画像", "", "", ""),
        new ShortcutAction("viewer.prev_image", "ビューアウィンドウ", "前の画像", "", "", ""),
        new ShortcutAction("viewer.save",       "ビューアウィンドウ", "保存",     "", "", ""),
        new ShortcutAction("viewer.close",      "ビューアウィンドウ", "閉じる",   "", "", ""),

        // NG 設定ウィンドウ
        new ShortcutAction("ng.add_rule",        "NG 設定ウィンドウ", "新規ルール追加",     "", "", ""),
        new ShortcutAction("ng.delete_selected", "NG 設定ウィンドウ", "選択中のルール削除", "", "", ""),
    };
}
