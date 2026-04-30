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

        // スレ一覧表示領域 (= 板のスレ一覧テーブル本文)
        new ShortcutAction("thread_list.refresh",                "スレ一覧表示領域", "選択中の板を更新",           "", "", ""),
        new ShortcutAction("thread_list.close_current_in_body",  "スレ一覧表示領域", "現在のスレ一覧タブを閉じる", "", "", ""),
        new ShortcutAction("thread_list.next_tab_in_body",       "スレ一覧表示領域", "次のタブ",                   "", "", ""),
        new ShortcutAction("thread_list.prev_tab_in_body",       "スレ一覧表示領域", "前のタブ",                   "", "", ""),
        new ShortcutAction("thread_list.scroll_top",             "スレ一覧表示領域", "最上部へスクロール",         "", "", ""),
        new ShortcutAction("thread_list.scroll_bottom",          "スレ一覧表示領域", "最下部へスクロール",         "", "", ""),

        // スレ一覧のタブ領域 (= 板タブのストリップ)
        new ShortcutAction("thread_list.close_current",  "スレ一覧のタブ領域", "現在のスレ一覧タブを閉じる", "", "", ""),
        new ShortcutAction("thread_list.next_tab",       "スレ一覧のタブ領域", "次のタブ",                   "", "", ""),
        new ShortcutAction("thread_list.prev_tab",       "スレ一覧のタブ領域", "前のタブ",                   "", "", ""),
        new ShortcutAction("thread_list.refresh_in_tab", "スレ一覧のタブ領域", "更新",                       "", "", ""),

        // スレッド表示領域 (= レス本文ビュー)
        new ShortcutAction("thread.refresh",                  "スレッド表示領域", "現在のスレを更新",       "", "", ""),
        new ShortcutAction("thread.new_thread",               "スレッド表示領域", "新規スレッド作成",       "", "", ""),
        new ShortcutAction("thread.scroll_top",               "スレッド表示領域", "最上部へスクロール",     "", "", ""),
        new ShortcutAction("thread.scroll_bottom",            "スレッド表示領域", "最下部へスクロール",     "", "", ""),
        new ShortcutAction("thread.delete_log",               "スレッド表示領域", "ログを削除",             "", "", ""),
        new ShortcutAction("thread.close_current_in_body",    "スレッド表示領域", "現在のスレッドタブを閉じる", "", "", ""),
        new ShortcutAction("thread.next_tab_in_body",         "スレッド表示領域", "次のタブ",               "", "", ""),
        new ShortcutAction("thread.prev_tab_in_body",         "スレッド表示領域", "前のタブ",               "", "", ""),

        // スレッドタブ表示領域 (= スレタブのストリップ)
        new ShortcutAction("thread.close_current",  "スレッドタブ表示領域", "現在のタブを閉じる", "", "", ""),
        new ShortcutAction("thread.next_tab",       "スレッドタブ表示領域", "次のタブ",           "", "", ""),
        new ShortcutAction("thread.prev_tab",       "スレッドタブ表示領域", "前のタブ",           "", "", ""),
        new ShortcutAction("thread.refresh_in_tab", "スレッドタブ表示領域", "更新",               "", "", ""),

        // ビューアウィンドウ。既定値は Phase 10 の元ハードコードを反映:
        // Esc=閉じる / Ctrl+S=保存 / ←=前 / →=次 / Ctrl+ホイールアップ=拡大 / Ctrl+ホイールダウン=縮小 /
        // ホイールアップ=前の画像 / ホイールダウン=次の画像
        new ShortcutAction("viewer.next_image",   "ビューアウィンドウ", "次の画像",     "Right",    "ホイールダウン",    ""),
        new ShortcutAction("viewer.prev_image",   "ビューアウィンドウ", "前の画像",     "Left",     "ホイールアップ",    ""),
        new ShortcutAction("viewer.save",         "ビューアウィンドウ", "保存",         "Ctrl+S",   "",                  ""),
        new ShortcutAction("viewer.close",        "ビューアウィンドウ", "閉じる",       "Escape",   "",                  ""),
        new ShortcutAction("viewer.zoom_in",      "ビューアウィンドウ", "拡大",         "",         "Ctrl+ホイールアップ", ""),
        new ShortcutAction("viewer.zoom_out",     "ビューアウィンドウ", "縮小",         "",         "Ctrl+ホイールダウン", ""),
        new ShortcutAction("viewer.rotate_right", "ビューアウィンドウ", "右に90度回転", "",         "",                  ""),
        new ShortcutAction("viewer.rotate_left",  "ビューアウィンドウ", "左に90度回転", "",         "",                  ""),
    };
}
