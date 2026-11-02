using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ChBrowser.Controls;

/// <summary>取得中を示す読み込みアイコン (回る 3/4 の円弧、10x10)。見えている間だけ回る。
/// XAML のテンプレート (DataTemplate) に書いた RotateTransform はテンプレートが凍結して動かせない
/// (アニメーションすると XamlParseException で落ちる) ので、回転は要素ごとにコードで作って回す。</summary>
public sealed class BusySpinner : FrameworkElement
{
    private const double Size = 10;

    private static readonly Geometry Arc = Freeze(Geometry.Parse("M5,1 A4,4 0 1 1 1,5"));
    private static readonly Pen ArcPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5))), 1.6)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap   = PenLineCap.Round,
    });

    private readonly RotateTransform _rotate = new();

    public BusySpinner()
    {
        RenderTransform       = _rotate;
        RenderTransformOrigin = new Point(0.5, 0.5);
        IsVisibleChanged     += (_, _) => UpdateAnimation();
    }

    private void UpdateAnimation()
    {
        if (IsVisible)
            _rotate.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        else
            _rotate.BeginAnimation(RotateTransform.AngleProperty, null);   // 見えない間は止める
    }

    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize) => new(Size, Size);

    protected override void OnRender(DrawingContext dc) => dc.DrawGeometry(null, ArcPen, Arc);

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
