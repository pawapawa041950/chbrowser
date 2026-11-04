using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using ChBrowser.ViewModels;

namespace ChBrowser.Controls;

/// <summary>スレの状態マーク: 状態の丸 (<see cref="LogMarkState"/> の色) の手前にお気に入りの ★ を重ね、
/// 取得中は丸の代わりに読み込みアイコンを最も手前に出す。取得に失敗したら丸を灰色にする (<see cref="IsFailed"/>)。
/// スレ表示ペインのタブとスレ一覧ペインのタブで使う。
/// スレ一覧ペインの行 (thread-list.css の <c>.log-mark</c>) も同じ大きさ・重ね順で描くので、変えるときは両方揃える。</summary>
public sealed class ThreadStateMark : Grid
{
    /// <summary>状態の丸の直径 (thread-list.css の .log-mark と同じ)。</summary>
    public const double DotSize  = 14;
    /// <summary>★ の文字サイズ (丸の中に収まり、周りに丸の色が見える大きさ)。WPF の既定フォントの ★ は WebView より大きく出るので、
    /// thread-list.css の .log-mark::after (12px) より小さい値で見た目を揃えている。</summary>
    public const double StarSize = 10.5;
    /// <summary>★ と状態の丸の色 (thread-list.css と同じ)。</summary>
    private static readonly Brush StarBrush         = Solid(0xE6, 0xB8, 0x00);
    private static readonly Brush CachedBrush       = Solid(0x1E, 0x88, 0xE5);   // 青: ログあり / 件数一致
    private static readonly Brush UpdatedBrush      = Solid(0x2E, 0x7D, 0x32);   // 緑: 新着あり
    private static readonly Brush DroppedBrush      = Solid(0x8D, 0x6E, 0x63);   // 茶: dat 落ち
    private static readonly Brush RepliedToOwnBrush = Solid(0xE5, 0x39, 0x35);   // 赤: 自分の書き込みへの返信あり
    private static readonly Brush FailedBrush       = Solid(0x9E, 0x9E, 0x9E);   // 灰: 取得失敗 (スレ一覧のタブ)

    private static Brush DotBrushOf(LogMarkState state) => state switch
    {
        LogMarkState.Cached       => CachedBrush,
        LogMarkState.Updated      => UpdatedBrush,
        LogMarkState.Dropped      => DroppedBrush,
        LogMarkState.RepliedToOwn => RepliedToOwnBrush,
        _                         => Brushes.Transparent,
    };

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(LogMarkState), typeof(ThreadStateMark),
        new PropertyMetadata(LogMarkState.None, (d, _) => ((ThreadStateMark)d).Update()));

    public static readonly DependencyProperty IsFavoritedProperty = DependencyProperty.Register(
        nameof(IsFavorited), typeof(bool), typeof(ThreadStateMark),
        new PropertyMetadata(false, (d, _) => ((ThreadStateMark)d).Update()));

    public static readonly DependencyProperty IsBusyProperty = DependencyProperty.Register(
        nameof(IsBusy), typeof(bool), typeof(ThreadStateMark),
        new PropertyMetadata(false, (d, _) => ((ThreadStateMark)d).Update()));

    /// <summary>取得に失敗した (丸を灰色にする。<see cref="State"/> より優先)。</summary>
    public static readonly DependencyProperty IsFailedProperty = DependencyProperty.Register(
        nameof(IsFailed), typeof(bool), typeof(ThreadStateMark),
        new PropertyMetadata(false, (d, _) => ((ThreadStateMark)d).Update()));

    public bool IsFailed { get => (bool)GetValue(IsFailedProperty); set => SetValue(IsFailedProperty, value); }

    public LogMarkState State  { get => (LogMarkState)GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public bool IsFavorited    { get => (bool)GetValue(IsFavoritedProperty);   set => SetValue(IsFavoritedProperty, value); }
    public bool IsBusy         { get => (bool)GetValue(IsBusyProperty);        set => SetValue(IsBusyProperty, value); }

    private readonly Ellipse     _dot     = new() { Width = DotSize, Height = DotSize };
    private readonly BusySpinner _spinner = new() { ToolTip = "取得中" };
    private readonly TextBlock   _star    = new()
    {
        Text = "★", FontSize = StarSize, Foreground = StarBrush,
        LineHeight = StarSize, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        IsHitTestVisible = false,
        // 白い縁取りで丸の色と分ける (thread-list.css の text-shadow と同じ)
        Effect = new DropShadowEffect { Color = Colors.White, ShadowDepth = 0, BlurRadius = 2, Opacity = 1 },
    };

    public ThreadStateMark()
    {
        Width  = DotSize;
        Height = DotSize;
        foreach (var e in new FrameworkElement[] { _dot, _star, _spinner })
        {
            e.HorizontalAlignment = HorizontalAlignment.Center;
            e.VerticalAlignment   = VerticalAlignment.Center;
            Children.Add(e);   // 後に足したものが手前 (丸 → ★ → 読み込みアイコンの順に手前)
        }
        Update();
    }

    private void Update()
    {
        _dot.Fill           = IsFailed ? FailedBrush : DotBrushOf(State);
        _dot.Visibility     = IsBusy ? Visibility.Collapsed : Visibility.Visible;
        _spinner.Visibility = IsBusy ? Visibility.Visible : Visibility.Collapsed;
        _star.Visibility    = IsFavorited ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Brush Solid(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
