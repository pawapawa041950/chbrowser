using System;
using System.Collections.Generic;

namespace ChBrowser.Models;

/// <summary>1 アクションに対するユーザのショートカット / マウス操作 / マウスジェスチャー設定 (Phase 15)。
/// 各フィールドが空文字なら「ユーザは明示的に未設定にした」を意味する (= 既定値にフォールバックしない)。
/// <see cref="Mouse"/> は "Ctrl+左クリック" / "Shift+ホイール上" 等の文字列。</summary>
public sealed record ShortcutBinding
{
    public string Id       { get; init; } = "";
    public string Shortcut { get; init; } = "";
    public string Mouse    { get; init; } = "";
    public string Gesture  { get; init; } = "";
}

/// <summary>ショートカット設定全体のシリアライズ単位。<c>data/app/shortcuts.json</c> に保存される。
/// <see cref="Bindings"/> はユーザが編集 + 保存したエントリのみを含む。
/// 含まれない Id は <see cref="ChBrowser.Services.Shortcuts.ShortcutRegistry"/> の既定値が適用される。</summary>
public sealed record ShortcutSettings
{
    public int Version { get; init; } = 1;
    public IReadOnlyList<ShortcutBinding> Bindings { get; init; } = Array.Empty<ShortcutBinding>();
}
