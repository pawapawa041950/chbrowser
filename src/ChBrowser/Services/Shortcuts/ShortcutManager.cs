using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Input;
using ChBrowser.Models;
using CommunityToolkit.Mvvm.Input;

namespace ChBrowser.Services.Shortcuts;

/// <summary>ショートカット / マウス操作 / マウスジェスチャーの「適用」と「ディスパッチ」を担う (Phase 15)。
///
/// <para>役割:
/// <list type="bullet">
/// <item><description><see cref="ShortcutSettings"/> + <see cref="ShortcutRegistry"/> から「実効バインド」を計算</description></item>
/// <item><description>キーバインドを <see cref="Window.InputBindings"/> に設定 / 再設定</description></item>
/// <item><description>マウス操作 (左/右/中クリック、ホイール上/下、+ 修飾キー) を MainWindow の Preview イベントで捕捉</description></item>
/// <item><description>ジェスチャー文字列 → action Id のマップを保持 (<see cref="GestureRecognizer"/> から呼ばれる)</description></item>
/// </list>
/// </para>
///
/// 現状はメインウィンドウのみが対象スコープ。ビューアウィンドウや NG ウィンドウなど
/// 他のウィンドウ固有のショートカットは後続フェーズで拡張する。</summary>
public sealed class ShortcutManager
{
    private readonly Window _mainWindow;
    private readonly Dictionary<string, Action<object?>> _handlers;
    private readonly List<KeyBinding> _appliedKeyBindings = new();
    // マウス操作 / ジェスチャーは (発生位置のペインカテゴリ, 操作/ジェスチャー文字列) → action Id でスコープ。
    // 例: ("スレ一覧のタブ領域", "ホイールアップ") → "thread_list.prev_tab"、
    //     ("スレッドタブ表示領域", "ホイールアップ") → "thread.prev_tab"
    // のように同じ操作文字列を別カテゴリに同時割当できる。発生位置 (= 開始位置) で振り分けるため、
    // 「タブ A がアクティブの状態でタブ B 上でホイール」のような操作も B が属するカテゴリで dispatch される。
    private readonly Dictionary<string, Dictionary<string, string>> _mouseByCategory   = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _gestureByCategory = new(StringComparer.Ordinal);

    public ShortcutManager(Window mainWindow, Dictionary<string, Action<object?>> handlers)
    {
        _mainWindow = mainWindow;
        _handlers   = handlers;

        // マウス操作はバインドが空のときも一度だけ subscribe (= 再 Apply で着脱しない、ディスパッチ時に dictionary lookup)。
        // PreviewMouseDown は左右中すべてのボタン押下、PreviewMouseUp は右ボタンのチョード状態解除、
        // PreviewMouseWheel はホイール回転で発火。
        _mainWindow.PreviewMouseDown  += OnPreviewMouseDown;
        _mainWindow.PreviewMouseUp    += OnPreviewMouseUp;
        _mainWindow.PreviewMouseWheel += OnPreviewMouseWheel;
    }

    /// <summary>設定を反映: 古い <see cref="KeyBinding"/> を削除して、現在の bindings を新たに登録。
    /// マウス / ジェスチャーマップも再構築する。</summary>
    public void Apply(ShortcutSettings settings)
    {
        foreach (var kb in _appliedKeyBindings)
            _mainWindow.InputBindings.Remove(kb);
        _appliedKeyBindings.Clear();
        _mouseByCategory.Clear();
        _gestureByCategory.Clear();

        foreach (var action in ShortcutRegistry.Actions)
        {
            var (shortcut, mouse, gesture) = ResolveEffective(action, settings);
            if (!_handlers.TryGetValue(action.Id, out var handler))
                continue; // 未実装アクションは skip

            if (!string.IsNullOrEmpty(shortcut) && TryParseShortcut(shortcut, out var key, out var mods))
            {
                // ショートカット (キーボード) はマウス source 無し。null を渡すとハンドラ側で SelectedTab フォールバック。
                var kb = new KeyBinding { Key = key, Modifiers = mods, Command = new RelayCommand(() => handler(null)) };
                _mainWindow.InputBindings.Add(kb);
                _appliedKeyBindings.Add(kb);
            }
            if (!string.IsNullOrEmpty(mouse))
            {
                if (!_mouseByCategory.TryGetValue(action.Category, out var mInner))
                {
                    mInner = new Dictionary<string, string>(StringComparer.Ordinal);
                    _mouseByCategory[action.Category] = mInner;
                }
                mInner[mouse] = action.Id;
            }
            if (!string.IsNullOrEmpty(gesture))
            {
                if (!_gestureByCategory.TryGetValue(action.Category, out var gInner))
                {
                    gInner = new Dictionary<string, string>(StringComparer.Ordinal);
                    _gestureByCategory[action.Category] = gInner;
                }
                gInner[gesture] = action.Id;
            }
        }
    }

    /// <summary>マウスジェスチャー認識器から呼ばれる。
    /// <paramref name="startCategory"/> はジェスチャー開始位置にあるペイン (= <see cref="ShortcutAction.Category"/> と同名)、
    /// <paramref name="source"/> は開始時の <c>e.OriginalSource</c> (= 開始位置にあったタブを解決するため)。
    /// 開始ペインに該当バインドが無ければ何もしない (= グローバルフォールバックは無し)。</summary>
    public bool DispatchGesture(string startCategory, string gesture, object? source)
        => DispatchScoped(_gestureByCategory, startCategory, gesture, source);

    private bool DispatchScoped(Dictionary<string, Dictionary<string, string>> byCategory, string category, string key, object? source)
    {
        if (string.IsNullOrEmpty(category) || string.IsNullOrEmpty(key)) return false;
        if (!byCategory.TryGetValue(category, out var inner)) return false;
        if (!inner.TryGetValue(key, out var actionId)) return false;
        if (!_handlers.TryGetValue(actionId, out var handler)) return false;
        try { handler(source); return true; }
        catch (Exception ex) { Debug.WriteLine($"[ShortcutManager] dispatch '{actionId}' failed: {ex.Message}"); return false; }
    }

    // 右クリックチョード検出用: 右ボタンが押下中かどうか。
    private bool _rightHeld;

    /// <summary>マウスボタン押下。
    /// <list type="bullet">
    /// <item><description>右ボタン → チョードフラグを立てるだけ (= ジェスチャーかチョードか単発右クリックかは離した時点で確定)</description></item>
    /// <item><description>右ホールド中 + 中ボタン → "右クリック+中ボタン" を試行 (失敗したら通常の中クリック処理にフォールスルー)</description></item>
    /// <item><description>左ボタン: ClickCount が 2 で「ダブルクリック」、3 で「トリプルクリック」 (単発左クリックは binding 不可)</description></item>
    /// <item><description>中ボタン: 修飾キー + 「中クリック」</description></item>
    /// </list></summary>
    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var btn = e.ChangedButton;

        if (btn == MouseButton.Right)
        {
            _rightHeld = true;
            return;
        }

        var category = CategoryResolver.Resolve(e.OriginalSource as DependencyObject);

        // 右ホールド中 + 中ボタン → チョード
        if (_rightHeld && btn == MouseButton.Middle)
        {
            if (DispatchScoped(_mouseByCategory, category, "右クリック+中ボタン", e.OriginalSource)) { e.Handled = true; return; }
            // 未割当ならフォールスルー (= 通常の中クリック処理を試す)
        }

        string? name = btn switch
        {
            MouseButton.Left   => e.ClickCount switch
            {
                2 => "ダブルクリック",
                3 => "トリプルクリック",
                _ => null,                  // 単発左クリックは binding 不可
            },
            MouseButton.Middle => "中クリック",
            _                  => null,
        };
        if (name is null) return;

        var key = BuildModifiedKey(Keyboard.Modifiers, name);
        if (DispatchScoped(_mouseByCategory, category, key, e.OriginalSource)) e.Handled = true;
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Right) _rightHeld = false;
    }

    /// <summary>ホイール回転 — Delta&gt;0 で「ホイールアップ」、それ以外で「ホイールダウン」。
    /// 右ホールド中なら「右クリック+ホイール...」を優先、未割当なら修飾キー組合せ版を試す。
    /// 発生位置のペインでスコープされる: 例えばスレ一覧タブストリップ上のホイールは
    /// 「スレ一覧のタブ領域」カテゴリのバインドだけが対象。</summary>
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var dir = e.Delta > 0 ? "ホイールアップ" : "ホイールダウン";
        var category = CategoryResolver.Resolve(e.OriginalSource as DependencyObject);

        if (_rightHeld)
        {
            if (DispatchScoped(_mouseByCategory, category, "右クリック+" + dir, e.OriginalSource)) { e.Handled = true; return; }
        }

        var key = BuildModifiedKey(Keyboard.Modifiers, dir);
        if (DispatchScoped(_mouseByCategory, category, key, e.OriginalSource)) e.Handled = true;
    }

    /// <summary>"Ctrl+Alt+Shift+左クリック" 等の文字列を組み立てる。修飾キー部分は <see cref="ShortcutEditDialog"/> /
    /// <see cref="MouseEditDialog"/> でユーザに表示するときと同じ並び順 / 同じ綴り (Ctrl→Alt→Shift→Win)。</summary>
    public static string BuildModifiedKey(ModifierKeys mods, string buttonOrWheelOrKey)
    {
        var sb = new StringBuilder();
        if ((mods & ModifierKeys.Control) != 0) sb.Append("Ctrl+");
        if ((mods & ModifierKeys.Alt)     != 0) sb.Append("Alt+");
        if ((mods & ModifierKeys.Shift)   != 0) sb.Append("Shift+");
        if ((mods & ModifierKeys.Windows) != 0) sb.Append("Win+");
        sb.Append(buttonOrWheelOrKey);
        return sb.ToString();
    }

    private static (string shortcut, string mouse, string gesture) ResolveEffective(ShortcutAction action, ShortcutSettings settings)
    {
        foreach (var b in settings.Bindings)
        {
            if (b.Id == action.Id) return (b.Shortcut, b.Mouse, b.Gesture);
        }
        return (action.DefaultShortcut, action.DefaultMouse, action.DefaultGesture);
    }

    /// <summary>"Ctrl+Alt+Shift+L" 形式の文字列を <see cref="Key"/> + <see cref="ModifierKeys"/> に分解。
    /// 失敗 (修飾だけ / 不明キー / 空) なら false を返す。</summary>
    public static bool TryParseShortcut(string s, out Key key, out ModifierKeys mods)
    {
        key  = Key.None;
        mods = ModifierKeys.None;
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var raw in s.Split('+'))
        {
            var part = raw.Trim();
            if (part.Length == 0) continue;
            switch (part)
            {
                case "Ctrl":
                case "Control": mods |= ModifierKeys.Control; break;
                case "Shift":   mods |= ModifierKeys.Shift;   break;
                case "Alt":     mods |= ModifierKeys.Alt;     break;
                case "Win":
                case "Windows": mods |= ModifierKeys.Windows; break;
                default:
                    if (Enum.TryParse<Key>(part, ignoreCase: true, out var k)) key = k;
                    break;
            }
        }
        return key != Key.None;
    }
}
