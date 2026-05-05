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

/// <summary>ChBrowser で扱うショートカット可能なアクションの全集合。
/// 既定値は開発者が日常使用しているプリセットを焼き付けたもの (=「shortcuts.json が無い新規環境」と
/// 「ユーザが既定に戻すボタンを押した時」の両方に適用される)。</summary>
public static class ShortcutRegistry
{
    public static IReadOnlyList<ShortcutAction> Actions { get; } = new[]
    {
        // 全体 (= 開始位置のペインに依存せず、画面のどこから入力しても発火するカテゴリ)。
        // ShortcutManager.Apply が他全カテゴリへマージし、pane 側に同 descriptor が無ければここの bind が効く。
        // 「全体」と pane で同じマウスジェスチャーを設定する事は ShortcutsWindow 側で禁止 (= マスクの誤解防止)。
        // 「全体」のマウス入力 (修飾 + クリック / ホイール) は通常 UI 操作と衝突しやすいので
        // 設定 UI 側で編集不可 (= CategoryResolver.IsMouseEditable で false)。
        new ShortcutAction("favorites.patrol",        "全体", "お気に入りを巡回",         "",       "", "↓←"),
        new ShortcutAction("main.focus_address_bar",  "全体", "アドレスバーへフォーカス", "Ctrl+L", "", ""),
        new ShortcutAction("main.refresh_board_list", "全体", "板一覧を更新",             "",       "", ""),
        new ShortcutAction("main.exit",               "全体", "終了",                     "",       "", ""),

        // スレ一覧表示領域 (= 板のスレ一覧テーブル本文)
        new ShortcutAction("thread_list.refresh",                "スレ一覧表示領域", "選択中の板を更新",           "", "",                          "↓→"),
        new ShortcutAction("thread_list.close_current_in_body",  "スレ一覧表示領域", "現在のスレ一覧タブを閉じる", "", "",                          "→←"),
        new ShortcutAction("thread_list.next_tab_in_body",       "スレ一覧表示領域", "次のタブ",                   "", "右クリック+ホイールダウン", ""),
        new ShortcutAction("thread_list.prev_tab_in_body",       "スレ一覧表示領域", "前のタブ",                   "", "右クリック+ホイールアップ", ""),
        new ShortcutAction("thread_list.scroll_top",             "スレ一覧表示領域", "最上部へスクロール",         "", "",                          "→↓"),
        new ShortcutAction("thread_list.scroll_bottom",          "スレ一覧表示領域", "最下部へスクロール",         "", "",                          "→↑"),

        // スレ一覧のタブ領域 (= 板タブのストリップ)。
        // close_current / refresh_in_tab の DefaultMouse は旧 AppConfig の click action 既定値を引き継ぐ
        // (中クリック=close / ダブルクリック=refresh)。close_others / close_left / close_right は
        // 旧 click action 設定の "closeOthers" / "closeLeft" / "closeRight" を移設したもの。
        new ShortcutAction("thread_list.close_current",  "スレ一覧のタブ領域", "現在のスレ一覧タブを閉じる",     "", "中クリック",     ""),
        new ShortcutAction("thread_list.close_others",   "スレ一覧のタブ領域", "他のスレ一覧タブをすべて閉じる", "", "",               ""),
        new ShortcutAction("thread_list.close_left",     "スレ一覧のタブ領域", "左のスレ一覧タブをすべて閉じる", "", "",               ""),
        new ShortcutAction("thread_list.close_right",    "スレ一覧のタブ領域", "右のスレ一覧タブをすべて閉じる", "", "",               ""),
        new ShortcutAction("thread_list.next_tab",       "スレ一覧のタブ領域", "次のタブ",                       "", "ホイールダウン", ""),
        new ShortcutAction("thread_list.prev_tab",       "スレ一覧のタブ領域", "前のタブ",                       "", "ホイールアップ", ""),
        new ShortcutAction("thread_list.refresh_in_tab", "スレ一覧のタブ領域", "更新",                           "", "ダブルクリック", ""),

        // スレッド表示領域 (= レス本文ビュー)
        new ShortcutAction("thread.refresh",                  "スレッド表示領域", "現在のスレを更新",           "", "",                          "↓→"),
        new ShortcutAction("thread.new_thread",               "スレッド表示領域", "新規スレッド作成",           "", "",                          ""),
        new ShortcutAction("thread.scroll_top",               "スレッド表示領域", "最上部へスクロール",         "", "",                          "→↓"),
        new ShortcutAction("thread.scroll_bottom",            "スレッド表示領域", "最下部へスクロール",         "", "",                          "→↑"),
        new ShortcutAction("thread.delete_log",               "スレッド表示領域", "ログを削除",                 "", "",                          "←↓→↑←"),
        new ShortcutAction("thread.close_current_in_body",    "スレッド表示領域", "現在のスレッドタブを閉じる", "", "",                          "→←"),
        new ShortcutAction("thread.next_tab_in_body",         "スレッド表示領域", "次のタブ",                   "", "右クリック+ホイールダウン", ""),
        new ShortcutAction("thread.prev_tab_in_body",         "スレッド表示領域", "前のタブ",                   "", "右クリック+ホイールアップ", ""),

        // スレッドタブ表示領域 (= スレタブのストリップ)。
        // close_current / refresh_in_tab の DefaultMouse は旧 AppConfig の click action 既定値を引き継ぐ。
        // add_favorite / delete_log_in_tab / close_others / close_left / close_right は
        // 旧 click action 設定の各 action 識別子を移設したもの。
        new ShortcutAction("thread.close_current",       "スレッドタブ表示領域", "現在のタブを閉じる",     "", "中クリック",     ""),
        new ShortcutAction("thread.add_favorite",        "スレッドタブ表示領域", "お気に入りに追加",       "", "",               ""),
        new ShortcutAction("thread.delete_log_in_tab",   "スレッドタブ表示領域", "ログを削除",             "", "",               ""),
        new ShortcutAction("thread.close_others",        "スレッドタブ表示領域", "他のタブをすべて閉じる", "", "",               ""),
        new ShortcutAction("thread.close_left",          "スレッドタブ表示領域", "左のタブをすべて閉じる", "", "",               ""),
        new ShortcutAction("thread.close_right",         "スレッドタブ表示領域", "右のタブをすべて閉じる", "", "",               ""),
        new ShortcutAction("thread.next_tab",            "スレッドタブ表示領域", "次のタブ",               "", "ホイールダウン", ""),
        new ShortcutAction("thread.prev_tab",            "スレッドタブ表示領域", "前のタブ",               "", "ホイールアップ", ""),
        new ShortcutAction("thread.refresh_in_tab",      "スレッドタブ表示領域", "更新",                   "", "ダブルクリック", ""),

        // ビューアウィンドウ。
        // 既定値は Phase 10 の元ハードコード + 開発者が日常運用しているプリセット:
        //   Esc / ダブルクリック / →← で閉じる、Ctrl+S=保存、←/ホイールアップ=前、→/ホイールダウン=次、
        //   Ctrl+ホイールアップ/ダウン=拡大/縮小、Shift+ホイールアップ/ダウン=左/右に 90 度回転、Tab=詳細ペイン切替
        new ShortcutAction("viewer.next_image",     "ビューアウィンドウ", "次の画像",     "Right",    "ホイールダウン",        ""),
        new ShortcutAction("viewer.prev_image",     "ビューアウィンドウ", "前の画像",     "Left",     "ホイールアップ",        ""),
        new ShortcutAction("viewer.save",           "ビューアウィンドウ", "保存",         "Ctrl+S",   "",                       ""),
        new ShortcutAction("viewer.close",          "ビューアウィンドウ", "閉じる",       "Escape",   "ダブルクリック",        "→←"),
        new ShortcutAction("viewer.zoom_in",        "ビューアウィンドウ", "拡大",         "",         "Ctrl+ホイールアップ",   ""),
        new ShortcutAction("viewer.zoom_out",       "ビューアウィンドウ", "縮小",         "",         "Ctrl+ホイールダウン",   ""),
        new ShortcutAction("viewer.rotate_right",   "ビューアウィンドウ", "右に90度回転", "",         "Shift+ホイールダウン",  ""),
        new ShortcutAction("viewer.rotate_left",    "ビューアウィンドウ", "左に90度回転", "",         "Shift+ホイールアップ",  ""),
        new ShortcutAction("viewer.toggle_details", "ビューアウィンドウ", "画像詳細ペインを表示/非表示", "Tab", "",              ""),
    };
}
