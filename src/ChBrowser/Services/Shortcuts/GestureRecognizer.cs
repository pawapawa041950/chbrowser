using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Input;

namespace ChBrowser.Services.Shortcuts;

/// <summary>マウスジェスチャー認識器 (Phase 15)。指定 <see cref="Window"/> 上での右ドラッグを追跡し、
/// 一定距離移動するごとに ↑↓←→ の 4 方向に量子化、連続同方向はマージして方向列を生成する。
///
/// <para>右ドラッグ <b>開始時点のペイン</b>を <see cref="ShortcutAction.Category"/> 名 (= "スレ一覧表示領域" 等) で判定し、
/// 認識完了時に <see cref="ShortcutManager.DispatchGesture"/> へ (start category, gesture) を渡す。
/// 同じジェスチャー文字列でも開始ペインが違えば別アクションが起動するスコープになっている。</para>
///
/// <para>制約: WebView2 (HwndHost) 内の右ドラッグは Win32 サーフェスに吸われるため WPF イベントが届かず、
/// この recognizer は動作しない。WebView2 内で使いたい場合は JS 経由の通知経路を別途追加する必要がある。</para></summary>
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
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_tracking) return;
        if (e.RightButton != MouseButtonState.Pressed) return;
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
            // ステータスバー: 現在の方向列を通知
            _manager.NotifyGestureProgress(_startCategory, new string(_directions.ToArray()));
        }
        _lastSamplePoint = p;
    }

    private void OnRightUp(object sender, MouseButtonEventArgs e)
    {
        if (!_tracking) return;
        _tracking = false;
        // ステータスバー: ジェスチャー終了通知 (= 表示クリア)
        _manager.NotifyGestureProgress(null, null);

        if (_directions.Count == 0) return; // 移動なし → 通常の右クリック扱い

        var sb = new StringBuilder();
        foreach (var c in _directions) sb.Append(c);
        var gesture = sb.ToString();

        // 認識した時のみ右ボタンアップを消費 (= コンテキストメニュー抑止)。
        if (_manager.DispatchGesture(_startCategory, gesture, _startSource))
            e.Handled = true;
    }
}
