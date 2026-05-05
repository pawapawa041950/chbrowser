using System.Collections.Generic;

namespace ChBrowser.Services.Shortcuts;

/// <summary>マウス操作 (=「Mouse」列) のドロップダウンに並べる選択肢一覧 (Phase 15)。
/// <see cref="Option.Label"/> は UI 表示用 (例: "Shift + 左クリック")、
/// <see cref="Option.Value"/> は <c>shortcuts.json</c> に保存される値および <see cref="ShortcutManager"/> の lookup キー
/// (例: "Shift+左クリック")。
///
/// 修飾なしの「左クリック」「右クリック」は通常 UI 操作と衝突するため意図的に外してある。
/// 「右クリック+...」はマウスチョード (右ボタンを押している間に他ボタン/ホイールを操作)。</summary>
public static class MouseOperationCatalog
{
    public sealed record Option(string Label, string Value);

    public static IReadOnlyList<Option> Options { get; } = new[]
    {
        new Option("(未設定)",                       ""),

        // 単体
        new Option("中クリック",                     "中クリック"),
        new Option("ホイールアップ",                 "ホイールアップ"),
        new Option("ホイールダウン",                 "ホイールダウン"),
        new Option("ダブルクリック",                 "ダブルクリック"),
        // トリプルクリックは原理的に「ダブルクリック 2 連発」と区別できず、誤発火源になるため非対応。

        // Shift +
        new Option("Shift + 左クリック",             "Shift+左クリック"),
        new Option("Shift + 右クリック",             "Shift+右クリック"),
        new Option("Shift + 中クリック",             "Shift+中クリック"),
        new Option("Shift + ホイールアップ",         "Shift+ホイールアップ"),
        new Option("Shift + ホイールダウン",         "Shift+ホイールダウン"),

        // Alt +
        new Option("Alt + 左クリック",               "Alt+左クリック"),
        new Option("Alt + 右クリック",               "Alt+右クリック"),
        new Option("Alt + 中クリック",               "Alt+中クリック"),
        new Option("Alt + ホイールアップ",           "Alt+ホイールアップ"),
        new Option("Alt + ホイールダウン",           "Alt+ホイールダウン"),

        // Ctrl +
        new Option("Ctrl + 左クリック",              "Ctrl+左クリック"),
        new Option("Ctrl + 右クリック",              "Ctrl+右クリック"),
        new Option("Ctrl + 中クリック",              "Ctrl+中クリック"),
        new Option("Ctrl + ホイールアップ",          "Ctrl+ホイールアップ"),
        new Option("Ctrl + ホイールダウン",          "Ctrl+ホイールダウン"),

        // 右クリックチョード (右ボタン押下中に副操作)
        new Option("右クリック + 中ボタン",          "右クリック+中ボタン"),
        new Option("右クリック + ホイールアップ",    "右クリック+ホイールアップ"),
        new Option("右クリック + ホイールダウン",    "右クリック+ホイールダウン"),
    };
}
