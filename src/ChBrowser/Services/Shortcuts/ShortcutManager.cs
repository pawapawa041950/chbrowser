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
    private Window? _viewerWindow;
    private ShortcutSettings? _lastSettings;
    private readonly Dictionary<string, Action<object?>> _handlers;
    // KeyBinding は Window 単位に登録し再 Apply 時にクリーンアップする。
    // ビューアウィンドウ用の KeyBinding は viewer 専用 Window へ、それ以外は MainWindow へ登録する。
    private readonly Dictionary<Window, List<KeyBinding>> _appliedKeyBindingsByWindow = new();

    // (発生位置のペインカテゴリ, 入力 descriptor) → action Id の統一マップ。
    // descriptor はキーボード ("Ctrl+L") / マウス操作 ("Ctrl+左クリック" / "ダブルクリック" / "ホイールアップ" /
    // "右クリック+中ボタン" 等) / ジェスチャー ("↓→") の各種文字列を区別なく格納する。
    // フォーマットが互いに重複しない (キーは英数主体、マウスは Japanese 名+「+」、ジェスチャーは Unicode 矢印) ため、
    // 1 つの dictionary で Webview ↔ WPF / キーボード ↔ マウス ↔ ジェスチャー を共通にディスパッチできる。
    private readonly Dictionary<string, Dictionary<string, string>> _inputsByCategory = new(StringComparer.Ordinal);

    /// <summary>Apply() 完了時に呼ばれる callback (Phase 16+)。
    /// 各カテゴリ (= ペイン) ごとの「ユーザが bind 済の descriptor → action Id マップ」を配信し、
    /// WebView2 内 JS ブリッジ等が「どの入力を suppress / dispatch するか」を判定するために使う。
    /// gesture descriptor も含める。</summary>
    public Action<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>? OnBindingsApplied { get; set; }

    /// <summary>ジェスチャー進捗通知 (Phase 16+)。
    /// (category, gesture) — 入力中なら category と現在の方向列、終了なら (null, null)。
    /// WPF の <see cref="GestureRecognizer"/> および各 WebView の JS ブリッジ両方からこれが呼ばれる。
    /// MainViewModel.GestureStatus を更新してステータスバーに表示する用途で使う。</summary>
    public Action<string?, string?>? OnGestureProgress { get; set; }

    /// <summary>外部 (WPF GestureRecognizer / WebView WebMessageReceived) から呼ばれる、進捗の中継。</summary>
    public void NotifyGestureProgress(string? category, string? gesture)
        => OnGestureProgress?.Invoke(category, gesture);

    /// <summary>指定カテゴリの descriptor → actionId マップを返す (Phase 16)。
    /// WebView 内 JS ブリッジが bridgeReady で C# に「初期化完了」を通知してきたとき、
    /// 該当 WebView だけに direct push し直すために使う。</summary>
    public IReadOnlyDictionary<string, string> GetBindingsForCategory(string category)
    {
        if (_inputsByCategory.TryGetValue(category, out var inner))
            return inner;
        return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>(category, descriptor) で発火するアクションの表示名を返す (= ShortcutAction.DisplayName)。
    /// マッチが無ければ null。マウスジェスチャー進捗のステータスバー表示で「右ボタンを離せばこのコマンドが発火」と
    /// 示すために使う。</summary>
    public string? TryGetActionDisplayName(string category, string descriptor)
    {
        if (string.IsNullOrEmpty(category) || string.IsNullOrEmpty(descriptor)) return null;
        if (!_inputsByCategory.TryGetValue(category, out var inner)) return null;
        if (!inner.TryGetValue(descriptor, out var actionId)) return null;
        foreach (var act in ShortcutRegistry.Actions)
            if (act.Id == actionId) return act.DisplayName;
        return null;
    }

    public ShortcutManager(Window mainWindow, Dictionary<string, Action<object?>> handlers)
    {
        _mainWindow = mainWindow;
        _handlers   = handlers;
        _appliedKeyBindingsByWindow[mainWindow] = new();

        // マウス操作はバインドが空のときも一度だけ subscribe (= 再 Apply で着脱しない、ディスパッチ時に dictionary lookup)。
        // PreviewMouseDown は左右中すべてのボタン押下で、PreviewMouseWheel はホイール回転で発火。
        // 右ボタンの hold 状態は Mouse.RightButton を直接参照するため独自トラッキングは不要。
        _mainWindow.PreviewMouseDown  += OnPreviewMouseDown;
        _mainWindow.PreviewMouseWheel += OnPreviewMouseWheel;
    }

    /// <summary>右ボタンが現在押下中か (Win32 のキー状態を直接参照)。
    /// WPF 側で右下げを観測したかどうかとは独立 (= WebView2 内で right-down → タブストリップで中クリック、
    /// のような境界跨ぎでも正しく true になる)。</summary>
    private static bool RightButtonHeld
        => Mouse.RightButton == MouseButtonState.Pressed;

    /// <summary>ビューアウィンドウ (= 別 Window) を ShortcutManager に登録 (Phase 16+)。
    /// "ビューアウィンドウ" カテゴリの KeyBinding がここへ登録される。
    /// マウス系は viewer の WebView 内 JS bridge → WebMessageReceived 経由で dispatch するため、
    /// ここでは event subscribe しない。viewer ウィンドウの chrome 上のマウス操作はサポートしない。</summary>
    public void AttachViewerWindow(Window viewerWindow)
    {
        if (ReferenceEquals(_viewerWindow, viewerWindow)) return;
        _viewerWindow = viewerWindow;
        if (!_appliedKeyBindingsByWindow.ContainsKey(viewerWindow))
            _appliedKeyBindingsByWindow[viewerWindow] = new();
        // 既に Apply 済なら viewer 用 KeyBinding を再生成
        if (_lastSettings is not null) Apply(_lastSettings);
    }

    private Window? GetWindowForCategory(string category)
        => category == "ビューアウィンドウ" ? _viewerWindow : _mainWindow;

    /// <summary>キーボードショートカット用の登録対象ウィンドウ列。
    /// 通常カテゴリは 1 ウィンドウだけ、「全体」は MainWindow + (attached なら) ViewerWindow の両方に登録して
    /// どちらにフォーカスがあっても発火するようにする。</summary>
    private IEnumerable<Window> GetKeyBindingTargetWindows(string category)
    {
        if (category == CategoryResolver.GlobalCategory)
        {
            yield return _mainWindow;
            if (_viewerWindow is { } v) yield return v;
            yield break;
        }
        if (GetWindowForCategory(category) is { } w) yield return w;
    }

    /// <summary>設定を反映: 古い <see cref="KeyBinding"/> を削除して、現在の bindings を新たに登録。
    /// マウス / ジェスチャーマップも再構築する。</summary>
    public void Apply(ShortcutSettings settings)
    {
        _lastSettings = settings;
        // 全ウィンドウから既存 KeyBinding を撤去
        foreach (var (window, kbs) in _appliedKeyBindingsByWindow)
        {
            foreach (var kb in kbs) window.InputBindings.Remove(kb);
            kbs.Clear();
        }
        _inputsByCategory.Clear();

        foreach (var action in ShortcutRegistry.Actions)
        {
            var (shortcut, mouse, gesture) = ResolveEffective(action, settings);
            // 設定 UI 側で編集禁止になっているもの (= 全体のマウス入力 / スレ一覧のタブ領域のジェスチャー)
            // が settings.json に残っていても登録しない。defense in depth。
            if (!CategoryResolver.IsMouseEditable(action.Category))   mouse   = "";
            if (!CategoryResolver.IsGestureEditable(action.Category)) gesture = "";
            if (!_handlers.TryGetValue(action.Id, out var handler))
                continue; // 未実装アクションは skip

            if (!string.IsNullOrEmpty(shortcut) && TryParseShortcut(shortcut, out var key, out var mods))
            {
                foreach (var targetWindow in GetKeyBindingTargetWindows(action.Category))
                {
                    if (!_appliedKeyBindingsByWindow.TryGetValue(targetWindow, out var kbs))
                    {
                        kbs = new List<KeyBinding>();
                        _appliedKeyBindingsByWindow[targetWindow] = kbs;
                    }
                    var kb = new KeyBinding { Key = key, Modifiers = mods, Command = new RelayCommand(() => handler(null)) };
                    targetWindow.InputBindings.Add(kb);
                    kbs.Add(kb);
                }
            }

            // descriptor 系 (shortcut/mouse/gesture) は全て同じ category 内に格納。
            // shortcut もここに入れる理由: WebView 内 JS ブリッジから keyboard descriptor を Dispatch する経路で同じマップを使うため。
            void AddTo(string descriptor)
            {
                if (string.IsNullOrEmpty(descriptor)) return;
                if (!_inputsByCategory.TryGetValue(action.Category, out var inner))
                {
                    inner = new Dictionary<string, string>(StringComparer.Ordinal);
                    _inputsByCategory[action.Category] = inner;
                }
                inner[descriptor] = action.Id;
            }
            AddTo(shortcut);
            AddTo(mouse);
            AddTo(gesture);
        }

        // 「全体」カテゴリのバインドを他全カテゴリへマージ (= ペイン側に同 descriptor が
        // 既にあれば pane 側が勝つ semantics)。CategoryResolver.AllCategories を target にすることで、
        // ShortcutRegistry に登録の無いカテゴリ (お気に入りペイン / 板一覧ペイン) にも届く。
        if (_inputsByCategory.TryGetValue(CategoryResolver.GlobalCategory, out var globalMap))
        {
            foreach (var cat in CategoryResolver.AllCategories)
            {
                if (cat == CategoryResolver.GlobalCategory) continue;
                if (!_inputsByCategory.TryGetValue(cat, out var inner))
                {
                    inner = new Dictionary<string, string>(StringComparer.Ordinal);
                    _inputsByCategory[cat] = inner;
                }
                foreach (var (descriptor, actionId) in globalMap)
                    if (!inner.ContainsKey(descriptor)) inner[descriptor] = actionId;
            }
        }

        // JS ブリッジ等の購読者へバインディング一覧を push (descriptor → actionId のマップで配信)。
        // JS 側はこのマップで「入力の descriptor → どの actionId か」を解決し、
        // local handler が定義されている actionId なら C# 経由せず処理 (= scroll 等)、
        // それ以外は C# に shortcut/gesture メッセージを送信する。
        if (OnBindingsApplied is { } cb)
        {
            var snapshot = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
            foreach (var (cat, inner) in _inputsByCategory)
                snapshot[cat] = new Dictionary<string, string>(inner, StringComparer.Ordinal);
            cb(snapshot);
        }
    }

    /// <summary>マウスジェスチャー認識器から呼ばれる。
    /// <paramref name="startCategory"/> はジェスチャー開始位置にあるペイン (= <see cref="ShortcutAction.Category"/> と同名)、
    /// <paramref name="source"/> は開始時の <c>e.OriginalSource</c> (= 開始位置にあったタブを解決するため)。
    /// 開始ペインに該当バインドが無ければ何もしない (= グローバルフォールバックは無し)。</summary>
    public bool DispatchGesture(string startCategory, string gesture, object? source)
        => DispatchScoped(_inputsByCategory, startCategory, gesture, source);

    /// <summary>WebView 内 JS ブリッジから呼ばれる汎用ディスパッチ。
    /// descriptor は キーボード ("Ctrl+L") / マウス ("Ctrl+左クリック" 等) / ジェスチャー ("↓→") いずれの形式でも OK。
    /// 該当 category にバインドが無ければ false。source は WebView 由来なので null 固定 (= タブ解決はせず SelectedTab フォールバック)。</summary>
    public bool Dispatch(string category, string descriptor)
        => DispatchScoped(_inputsByCategory, category, descriptor, source: null);

    private bool DispatchScoped(Dictionary<string, Dictionary<string, string>> byCategory, string category, string key, object? source)
    {
        if (string.IsNullOrEmpty(category) || string.IsNullOrEmpty(key)) return false;
        if (!byCategory.TryGetValue(category, out var inner)) return false;
        if (!inner.TryGetValue(key, out var actionId)) return false;
        if (!_handlers.TryGetValue(actionId, out var handler)) return false;
        try { handler(source); return true; }
        catch (Exception ex) { Debug.WriteLine($"[ShortcutManager] dispatch '{actionId}' failed: {ex.Message}"); return false; }
    }

    /// <summary>マウスボタン押下。
    /// <list type="bullet">
    /// <item><description>右ボタン → 何もしない (= 単発右クリック / ジェスチャー / チョードの判定は離した時点 / 後続の入力で確定)</description></item>
    /// <item><description>右ホールド中 + 中ボタン → "右クリック+中ボタン" を試行 (失敗したら通常の中クリック処理にフォールスルー)</description></item>
    /// <item><description>左ボタン: ClickCount が 2 で「ダブルクリック」 (単発左クリック / トリプルクリックは非対応)</description></item>
    /// <item><description>中ボタン: 修飾キー + 「中クリック」</description></item>
    /// </list>
    /// トリプルクリックは「ダブルクリック 2 連発」と原理的に区別できず誤発火源になるため意図的に外している。</summary>
    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var btn = e.ChangedButton;
        if (btn == MouseButton.Right) return;

        var category = CategoryResolver.Resolve(e.OriginalSource as DependencyObject);

        if (RightButtonHeld && btn == MouseButton.Middle)
        {
            if (DispatchScoped(_inputsByCategory, category, "右クリック+中ボタン", e.OriginalSource)) { e.Handled = true; return; }
            // 未割当ならフォールスルー (= 通常の中クリック処理を試す)
        }

        // 左ボタン: ダブルクリックは "ダブルクリック"、修飾キー付き単発は "<Mod>+左クリック"。
        // 修飾なし単発左クリックは通常 UI 操作と衝突するので bind 対象外 (= null で抜ける)。
        var hasMod = Keyboard.Modifiers != ModifierKeys.None;
        string? name = btn switch
        {
            MouseButton.Left   => e.ClickCount == 2 ? "ダブルクリック"
                                : (e.ClickCount == 1 && hasMod ? "左クリック" : null),
            MouseButton.Middle => "中クリック",
            _                  => null,
        };
        if (name is null) return;

        var key = BuildModifiedKey(Keyboard.Modifiers, name);
        if (DispatchScoped(_inputsByCategory, category, key, e.OriginalSource)) e.Handled = true;
    }

    /// <summary>ホイール回転 — Delta&gt;0 で「ホイールアップ」、それ以外で「ホイールダウン」。
    /// 右ホールド中なら「右クリック+ホイール...」を優先、未割当なら修飾キー組合せ版を試す。
    /// 発生位置のペインでスコープされる: 例えばスレ一覧タブストリップ上のホイールは
    /// 「スレ一覧のタブ領域」カテゴリのバインドだけが対象。</summary>
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var dir = e.Delta > 0 ? "ホイールアップ" : "ホイールダウン";
        var category = CategoryResolver.Resolve(e.OriginalSource as DependencyObject);

        if (RightButtonHeld)
        {
            if (DispatchScoped(_inputsByCategory, category, "右クリック+" + dir, e.OriginalSource)) { e.Handled = true; return; }
        }

        var key = BuildModifiedKey(Keyboard.Modifiers, dir);
        if (DispatchScoped(_inputsByCategory, category, key, e.OriginalSource)) e.Handled = true;
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
