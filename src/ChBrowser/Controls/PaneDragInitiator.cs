using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ChBrowser.Models;

namespace ChBrowser.Controls;

/// <summary>各ペイン UserControl のヘッダ要素に「ドラッグ開始」ロジックを付ける小さなヘルパ (Phase 23)。
///
/// <para>仕組み: ヘッダで左クリック → 閾値超えて移動 → 親方向に <see cref="PaneLayoutPanel"/> を探して
/// <see cref="PaneLayoutPanel.BeginPaneDrag"/> を呼ぶ。<see cref="PaneLayoutPanel"/> は <c>CaptureMouse</c>
/// で以降のマウスイベントを引き受け、WebView2 (HwndHost) を跨いだ場所への drop も可能にする。</para>
///
/// <para>OLE DragDrop (<see cref="System.Windows.DragDrop.DoDragDrop"/>) を使わない理由: HwndHost 子 HWND
/// (= WebView2) 上では DragOver / Drop イベントが届かず、「ヘッダ部分でしか drop できない」という
/// 大きなUX問題があった (= GestureRecognizer の WebView2 跨ぎ問題と同じ系統)。</para></summary>
public static class PaneDragInitiator
{
    private const double DragThreshold = 4.0;

    public static void Attach(FrameworkElement header, PaneId paneId)
    {
        Point? downPoint = null;
        bool   armed     = false;

        header.PreviewMouseLeftButtonDown += (s, e) =>
        {
            // 子要素の Button (× / ヘッダ右端のリフレッシュ等) で押されていたらドラッグ開始しない。
            if (e.OriginalSource is DependencyObject src && IsInsideButton(src))
            {
                downPoint = null;
                return;
            }
            downPoint = e.GetPosition(header);
            armed     = true;
        };
        header.PreviewMouseMove += (s, e) =>
        {
            if (!armed) return;
            if (downPoint is not Point start) return;
            if (e.LeftButton != MouseButtonState.Pressed) { armed = false; return; }
            var p  = e.GetPosition(header);
            var dx = p.X - start.X;
            var dy = p.Y - start.Y;
            if ((dx * dx + dy * dy) < DragThreshold * DragThreshold) return;
            // 閾値超え → 自前ドラッグ開始 (Mouse.Capture が PaneLayoutPanel に移る)
            armed     = false;
            downPoint = null;
            FindAncestorPanel(header)?.BeginPaneDrag(paneId);
        };
        header.PreviewMouseLeftButtonUp += (s, e) =>
        {
            armed     = false;
            downPoint = null;
        };
    }

    private static PaneLayoutPanel? FindAncestorPanel(DependencyObject d)
    {
        DependencyObject? cur = d;
        while (cur is not null)
        {
            if (cur is PaneLayoutPanel p) return p;
            cur = VisualTreeHelper.GetParent(cur) ?? LogicalTreeHelper.GetParent(cur);
        }
        return null;
    }

    private static bool IsInsideButton(DependencyObject d)
    {
        DependencyObject? cur = d;
        while (cur is not null)
        {
            if (cur is System.Windows.Controls.Primitives.ButtonBase) return true;
            cur = VisualTreeHelper.GetParent(cur);
        }
        return false;
    }
}
