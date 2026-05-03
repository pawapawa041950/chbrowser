using System.Windows;
using System.Windows.Media;

namespace ChBrowser.Controls;

/// <summary>D&amp;D 中、ドロップ先のターゲット leaf 上に「ここに置くとこうなる」プレビューを描く軽量要素 (Phase 23)。
///
/// <para>WebView2 (HwndHost) は Win32 island で WPF の <see cref="System.Windows.Documents.AdornerLayer"/> よりも
/// 後段で描画されるため、Adorner を使うと HwndHost エリアでオーバーレイが裏に隠れる (= airspace 問題)。
/// この要素は <see cref="PaneLayoutPanel"/> の管理する <see cref="System.Windows.Controls.Primitives.Popup"/> 内に
/// 入れ、Popup は別 HWND として常に最前面に表示されるので HwndHost を跨いだ overlay 描画ができる。</para></summary>
public sealed class PaneDropZoneOverlay : FrameworkElement
{
    private Rect? _zone;

    private static readonly Brush ZoneBrush = new SolidColorBrush(Color.FromArgb(96, 30, 144, 255));
    private static readonly Pen   ZonePen   = new Pen(new SolidColorBrush(Color.FromArgb(180, 30, 144, 255)), 2.0);

    static PaneDropZoneOverlay()
    {
        ZoneBrush.Freeze();
        ZonePen.Freeze();
    }

    public PaneDropZoneOverlay()
    {
        IsHitTestVisible = false; // overlay 自体はマウスを奪わない
    }

    public void Update(Rect? zoneRect)
    {
        if (_zone == zoneRect) return;
        _zone = zoneRect;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_zone is not Rect r) return;
        drawingContext.DrawRectangle(ZoneBrush, ZonePen, r);
    }
}
