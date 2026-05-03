using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Input;

namespace ChBrowser.Services.Shortcuts;

/// <summary>マウスジェスチャー認識器 (Phase 15)。指定 <see cref="Window"/> 上での右ドラッグを追跡し、
/// 一定距離移動するごとに ↑↓←→ の 4 方向に量子化、連続同方向はマージして方向列を生成する。
///
/// <para>右ドラッグ <b>開始時点のペイン</b>を <see cref="ShortcutAction.Category"/> 名 (= "スレ一覧表示領域" 等)
/// で判定し、認識完了時に <see cref="ShortcutManager.DispatchGesture"/> へ (start category, gesture) を渡す。
/// 同じジェスチャー文字列でも開始ペインが違えば別アクションが起動するスコープになっている。</para>
///
/// <para>WebView2 (HwndHost) 跨ぎ: 通常 WebView2 は Win32 surface でマウス入力を吸収するため、
/// WPF 側で開始したジェスチャーがカーソル WebView 進入時点で止まる。これを防ぐため右押下時に
/// <see cref="UIElement.CaptureMouse"/> でウィンドウ HWND に capture を取り、WebView2 の子 HWND が
/// マウス入力を奪わないようにする。<see cref="UIElement.LostMouseCapture"/> で graceful cancel。</para>
///
/// <para>WebView 内で開始したジェスチャー (= 右押下を WPF が観測しない) は、各 WebView2 の
/// <c>shortcut-bridge.js</c> がドキュメントレベルで認識する。こちらは無関与。</para></summary>
public sealed class GestureRecognizer
{
    private readonly Window          _window;
    private readonly ShortcutManager _manager;

    private bool         _tracking;
    private Point        _lastSamplePoint;
    private const double SampleDistance = 18.0;
    private readonly List<char> _directions = new();
    private string       _startCategory = "メインウィンドウ";
    private DependencyObject? _startSource;

    public GestureRecognizer(Window window, ShortcutManager manager)
    {
        _window  = window;
        _manager = manager;
        window.PreviewMouseRightButtonDown += OnRightDown;
        window.PreviewMouseMove            += OnMove;
        window.PreviewMouseRightButtonUp   += OnRightUp;
        window.LostMouseCapture            += OnLostCapture;
    }

    private void OnRightDown(object sender, MouseButtonEventArgs e)
    {
        _tracking        = true;
        _lastSamplePoint = e.GetPosition(_window);
        _directions.Clear();
        _startSource     = e.OriginalSource as DependencyObject;
        _startCategory   = CategoryResolver.Resolve(_startSource);
        // ステータスバー: ジェスチャー入力開始を通知 (= 空の方向列)
        _manager.NotifyGestureProgress(_startCategory, "");

        // WebView2 (HwndHost) 跨ぎ対策: ウィンドウ HWND にマウスキャプチャを取り、
        // カーソルが WebView2 の上に行っても mousemove が引き続き届くようにする。
        // 失敗 (= 既に他要素が capture 中等) しても tracking は続行する (WPF 領域内では機能する)。
        _window.CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_tracking) return;

        // capture が外れた等で右ボタン状態と乖離したら graceful にキャンセル
        if (e.RightButton != MouseButtonState.Pressed)
        {
            StopTracking();
            return;
        }

        var p = e.GetPosition(_window);
        var dx = p.X - _lastSamplePoint.X;
        var dy = p.Y - _lastSamplePoint.Y;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < SampleDistance) return;

        char dir = Math.Abs(dx) > Math.Abs(dy)
            ? (dx > 0 ? '→' : '←')
            : (dy > 0 ? '↓' : '↑');
        if (_directions.Count == 0 || _directions[^1] != dir)
        {
            _directions.Add(dir);
            _manager.NotifyGestureProgress(_startCategory, new string(_directions.ToArray()));
        }
        _lastSamplePoint = p;
    }

    private void OnRightUp(object sender, MouseButtonEventArgs e)
    {
        if (!_tracking) return;
        StopTracking();

        if (_directions.Count == 0) return; // 移動なし → 通常の右クリック扱い

        var sb = new StringBuilder();
        foreach (var c in _directions) sb.Append(c);
        var gesture = sb.ToString();

        // 認識した時のみ右ボタンアップを消費 (= コンテキストメニュー抑止)。
        if (_manager.DispatchGesture(_startCategory, gesture, _startSource))
            e.Handled = true;
    }

    /// <summary>capture が外部要因 (Alt+Tab / 別要素の Mouse.Capture / モーダル表示等) で
    /// 失われた場合のクリーンアップ。tracking 中なら graceful cancel。</summary>
    private void OnLostCapture(object sender, MouseEventArgs e)
    {
        if (_tracking) StopTracking();
    }

    /// <summary>tracking 状態の終了処理を一箇所に集約。
    /// 右上 / capture 喪失 / 右ボタン状態の乖離検知 すべてここを通る。</summary>
    private void StopTracking()
    {
        _tracking = false;
        if (_window.IsMouseCaptured) _window.ReleaseMouseCapture();
        _manager.NotifyGestureProgress(null, null);
    }
}
