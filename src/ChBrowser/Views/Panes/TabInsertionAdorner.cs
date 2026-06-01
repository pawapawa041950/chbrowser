using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace ChBrowser.Views.Panes;

/// <summary>タブ D&D 中に「ここに挿入される」位置を示す縦の太線 (VS Code 風) を描く Adorner。
/// 対象タブの左端 (= before) または右端 (= after) に太線 + 上下三角キャップを引く。
/// <see cref="IsHitTestVisible"/>=false なので D&D のヒットテスト (DragOver/Drop) を邪魔しない。</summary>
internal sealed class TabInsertionAdorner : Adorner
{
    private const double LineThickness = 3.0;
    private const double CapSize       = 4.0;

    private readonly bool _after;
    private static readonly Brush s_brush = CreateBrush();
    private static readonly Pen   s_pen   = CreatePen();

    public TabInsertionAdorner(UIElement adornedElement, bool after) : base(adornedElement)
    {
        _after           = after;
        IsHitTestVisible = false;
    }

    private static Brush CreateBrush()
    {
        var b = new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5)); // 設定ペインの選択色と同系
        b.Freeze();
        return b;
    }

    private static Pen CreatePen()
    {
        var p = new Pen(s_brush, LineThickness);
        p.Freeze();
        return p;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = AdornedElement.RenderSize.Width;
        var h = AdornedElement.RenderSize.Height;

        // 端で線が半分はみ出さないよう内側に LineThickness/2 寄せる。
        double x = _after ? w - LineThickness / 2 : LineThickness / 2;

        dc.DrawLine(s_pen, new Point(x, CapSize), new Point(x, h - CapSize));

        // 上下の三角キャップ (VS Code 風)。線方向に向かって閉じる小さな三角。
        DrawCap(dc, x, top: true);
        DrawCap(dc, x, top: false);
    }

    private void DrawCap(DrawingContext dc, double x, bool top)
    {
        double yBase = top ? 0 : AdornedElement.RenderSize.Height;
        double yTip  = top ? CapSize : AdornedElement.RenderSize.Height - CapSize;
        var fig = new PathFigure
        {
            StartPoint = new Point(x - CapSize, yBase),
            IsClosed   = true,
            IsFilled   = true,
        };
        fig.Segments.Add(new LineSegment(new Point(x + CapSize, yBase), false));
        fig.Segments.Add(new LineSegment(new Point(x, yTip), false));
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        geo.Freeze();
        dc.DrawGeometry(s_brush, null, geo);
    }
}
