using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ChBrowser.Models;

namespace ChBrowser.Controls;

/// <summary>4 ペインを binary tree のレイアウトに従って配置するカスタム Panel (Phase 23 docking)。
///
/// <para>WPF の通常の Grid / DockPanel と違い、children の親子関係は固定 (= 直接の子はずっと PaneLayoutPanel で
/// 変わらない)。レイアウト変化時は <see cref="PaneLayoutPanel.Layout"/> プロパティを書き換えて
/// <see cref="UIElement.InvalidateArrange"/> を呼ぶだけで、視覚的な配置だけが更新される。
/// これにより HwndHost (WebView2) が再生成されず、フリッカや state ロスが発生しない。</para>
///
/// <para>各 child は <see cref="PaneIdProperty"/> 添付プロパティで自分の <see cref="Models.PaneId"/> を表明する。
/// PaneLayoutPanel は Layout ツリーを走査して各 leaf に対応する child を見つけ、計算した Rect に配置する。
/// 4 leaf に対応する child が揃っていない場合は WPF Visual の上限に従い見えるものだけ配置する (= debug 補助)。</para>
///
/// <para>スプリッタの drag は <c>OnPreviewMouseLeftButtonDown</c> / <c>OnPreviewMouseMove</c> /
/// <c>OnPreviewMouseLeftButtonUp</c> でハンドル領域 (= split 境界の幅 4 px の帯) を hit-test して、
/// 該当 SplitNode の Ratio をリアルタイム更新する。</para></summary>
public class PaneLayoutPanel : Panel
{
    /// <summary>各 child が「自分は <see cref="Models.PaneId"/> = X だ」と表明するための添付プロパティ。
    /// PaneLayoutPanel が leaf 配置時に使う。</summary>
    public static readonly DependencyProperty PaneIdProperty =
        DependencyProperty.RegisterAttached(
            "PaneId",
            typeof(PaneId?),
            typeof(PaneLayoutPanel),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static PaneId? GetPaneId(DependencyObject d) => (PaneId?)d.GetValue(PaneIdProperty);
    public static void    SetPaneId(DependencyObject d, PaneId? value) => d.SetValue(PaneIdProperty, value);

    /// <summary>レイアウトツリー。null なら何も配置しない (= 空ペイン状態)。
    /// セッターで <see cref="UIElement.InvalidateArrange"/> + <see cref="UIElement.InvalidateMeasure"/> を呼ぶ。</summary>
    public LayoutNode? Layout
    {
        get => _layout;
        set
        {
            if (ReferenceEquals(_layout, value)) return;
            _layout = value;
            InvalidateMeasure();
            InvalidateArrange();
        }
    }
    private LayoutNode? _layout;

    /// <summary>スプリッタの hit-test 太さ (px)。実際の splitter 視覚要素はこれと同じ太さで描画する。</summary>
    public double SplitterThickness { get; set; } = 4.0;

    /// <summary>各 leaf の最小サイズ (px)。リサイズで Ratio が更新されるとき、これを下回らないよう clamp する。</summary>
    public double MinPaneSize { get; set; } = 80.0;

    /// <summary>レイアウトが変化 (drag による ratio 変化 等) したときに上位へ通知するためのイベント。
    /// 永続化トリガに使う。</summary>
    public event EventHandler? LayoutChanged;

    private void RaiseLayoutChanged() => LayoutChanged?.Invoke(this, EventArgs.Empty);

    // ---- 各 leaf の配置領域を保持 (再描画間で再利用しない、毎回 ArrangeOverride で再計算) ----
    private readonly Dictionary<PaneId, Rect>             _leafRects     = new();
    private readonly List<(SplitLayoutNode Node, Rect Bounds, bool IsHorizontal)> _splitterRects = new();

    // ---- ドラッグ中のスプリッタ ----
    private SplitLayoutNode? _draggingSplitter;
    private Rect             _draggingContainerRect;
    private bool             _draggingHorizontal;
    private Point            _dragStartPoint;
    private double           _dragStartRatio;

    protected override Size MeasureOverride(Size availableSize)
    {
        // 子の Measure は実際のサイズを我々が決めるので「大きめに」許容する。
        var measure = new Size(
            double.IsInfinity(availableSize.Width)  ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(measure);
        }
        return measure;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _leafRects.Clear();
        _splitterRects.Clear();

        var bounds = new Rect(0, 0, finalSize.Width, finalSize.Height);
        if (_layout is not null)
            ComputeLayout(_layout, bounds);

        foreach (UIElement child in InternalChildren)
        {
            var paneId = GetPaneId(child);
            if (paneId is PaneId pid && _leafRects.TryGetValue(pid, out var rect))
            {
                child.Arrange(rect);
            }
            else
            {
                // PaneId が割当てられていない / レイアウトに含まれない子は隠す
                child.Arrange(new Rect(0, 0, 0, 0));
            }
        }

        return finalSize;
    }

    /// <summary>レイアウトツリーを再帰的に走査して各 leaf の配置 Rect を <see cref="_leafRects"/> に登録、
    /// split 境界に対応する hit-test 用 Rect を <see cref="_splitterRects"/> に登録する。</summary>
    private void ComputeLayout(LayoutNode node, Rect rect)
    {
        if (node is LeafLayoutNode leaf)
        {
            _leafRects[leaf.Pane] = rect;
            return;
        }
        if (node is not SplitLayoutNode split) return;

        // ratio はそのままだと clamp 不足の場合があるので確実に clamp
        var ratio = Math.Clamp(split.Ratio, 0.05, 0.95);
        if (split.Orientation == LayoutOrientation.Horizontal)
        {
            // 左右分割: First が左、Second が右
            var splitterW = SplitterThickness;
            var totalW    = Math.Max(0, rect.Width - splitterW);
            var firstW    = totalW * ratio;
            var firstRect  = new Rect(rect.X, rect.Y, firstW, rect.Height);
            var splitterRect = new Rect(rect.X + firstW, rect.Y, splitterW, rect.Height);
            var secondRect = new Rect(rect.X + firstW + splitterW, rect.Y, totalW - firstW, rect.Height);
            ComputeLayout(split.First, firstRect);
            _splitterRects.Add((split, splitterRect, true));
            ComputeLayout(split.Second, secondRect);
        }
        else
        {
            // 上下分割: First が上、Second が下
            var splitterH = SplitterThickness;
            var totalH    = Math.Max(0, rect.Height - splitterH);
            var firstH    = totalH * ratio;
            var firstRect  = new Rect(rect.X, rect.Y, rect.Width, firstH);
            var splitterRect = new Rect(rect.X, rect.Y + firstH, rect.Width, splitterH);
            var secondRect = new Rect(rect.X, rect.Y + firstH + splitterH, rect.Width, totalH - firstH);
            ComputeLayout(split.First, firstRect);
            _splitterRects.Add((split, splitterRect, false));
            ComputeLayout(split.Second, secondRect);
        }
    }

    /// <summary>境界線 (= splitter rect) を背景色で描画。</summary>
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var brush = SystemColors.ControlBrush;
        foreach (var (_, rect, _) in _splitterRects)
        {
            dc.DrawRectangle(brush, null, rect);
        }
    }

    // ---- スプリッタドラッグ ----

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        var p = e.GetPosition(this);
        foreach (var (node, rect, horizontal) in _splitterRects)
        {
            if (rect.Contains(p))
            {
                _draggingSplitter      = node;
                _draggingHorizontal    = horizontal;
                _draggingContainerRect = ComputeContainerRect(node);
                _dragStartPoint        = p;
                _dragStartRatio        = node.Ratio;
                CaptureMouse();
                e.Handled = true;
                return;
            }
        }
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);

        // ペインドラッグ中: drop zone visualization のみ更新 (splitter 系処理はスキップ)
        if (_isPaneDragging)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndPaneDrag();
                return;
            }
            UpdateDragVisual(e.GetPosition(this));
            return;
        }

        // hover でカーソルを変える
        if (_draggingSplitter is null)
        {
            var p = e.GetPosition(this);
            Cursor? cursor = null;
            foreach (var (_, rect, horizontal) in _splitterRects)
            {
                if (rect.Contains(p))
                {
                    cursor = horizontal ? Cursors.SizeWE : Cursors.SizeNS;
                    break;
                }
            }
            Cursor = cursor;
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndSplitterDrag();
            return;
        }

        var pos = e.GetPosition(this);
        var split = _draggingSplitter;
        if (split is null) return;

        // 親 SplitNode の container rect 内での新 ratio を計算
        var bounds = _draggingContainerRect;
        double newRatio;
        if (_draggingHorizontal)
        {
            // 左右分割: container 内の x 位置が ratio
            var availW = Math.Max(0, bounds.Width - SplitterThickness);
            if (availW <= 0) return;
            var rel = pos.X - bounds.X;
            newRatio = rel / availW;
        }
        else
        {
            var availH = Math.Max(0, bounds.Height - SplitterThickness);
            if (availH <= 0) return;
            var rel = pos.Y - bounds.Y;
            newRatio = rel / availH;
        }

        // MinPaneSize ガード: 両側がそれぞれ MinPaneSize 以上残る範囲に clamp
        double minRatio, maxRatio;
        if (_draggingHorizontal)
        {
            var availW = Math.Max(0, bounds.Width - SplitterThickness);
            if (availW <= MinPaneSize * 2) { minRatio = 0.05; maxRatio = 0.95; }
            else { minRatio = MinPaneSize / availW; maxRatio = 1.0 - minRatio; }
        }
        else
        {
            var availH = Math.Max(0, bounds.Height - SplitterThickness);
            if (availH <= MinPaneSize * 2) { minRatio = 0.05; maxRatio = 0.95; }
            else { minRatio = MinPaneSize / availH; maxRatio = 1.0 - minRatio; }
        }
        newRatio = Math.Clamp(newRatio, minRatio, maxRatio);
        if (Math.Abs(newRatio - split.Ratio) < 1e-4) return;
        split.Ratio = newRatio;
        InvalidateArrange();
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (_isPaneDragging)
        {
            TryCommitPaneDrop(e.GetPosition(this));
            EndPaneDrag();
            e.Handled = true;
            return;
        }
        if (_draggingSplitter is not null)
        {
            EndSplitterDrag();
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_isPaneDragging)      EndPaneDrag();
        if (_draggingSplitter is not null) EndSplitterDrag();
    }

    private void EndSplitterDrag()
    {
        if (_draggingSplitter is null) return;
        _draggingSplitter = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        Cursor = null;
        RaiseLayoutChanged();
    }

    /// <summary>指定の split node の "container rect" (= その split が占める領域全体の Rect) を返す。
    /// container rect は ComputeLayout でその split を計算した時の入力 rect だが、
    /// ここでは <see cref="_splitterRects"/> 経由では取れないので、再走査して求める。</summary>
    private Rect ComputeContainerRect(SplitLayoutNode target)
    {
        if (_layout is null) return new Rect(0, 0, ActualWidth, ActualHeight);
        var found = new[] { Rect.Empty };
        Walk(_layout, new Rect(0, 0, ActualWidth, ActualHeight), target, found);
        return found[0];
    }

    private static bool Walk(LayoutNode node, Rect rect, SplitLayoutNode target, Rect[] found)
    {
        if (ReferenceEquals(node, target)) { found[0] = rect; return true; }
        if (node is not SplitLayoutNode s) return false;
        var ratio = Math.Clamp(s.Ratio, 0.05, 0.95);
        var thickness = 4.0; // SplitterThickness を引数で渡してもいいが固定値で十分
        if (s.Orientation == LayoutOrientation.Horizontal)
        {
            var availW = Math.Max(0, rect.Width - thickness);
            var firstW = availW * ratio;
            var firstRect = new Rect(rect.X, rect.Y, firstW, rect.Height);
            var secondRect = new Rect(rect.X + firstW + thickness, rect.Y, availW - firstW, rect.Height);
            return Walk(s.First, firstRect, target, found) || Walk(s.Second, secondRect, target, found);
        }
        else
        {
            var availH = Math.Max(0, rect.Height - thickness);
            var firstH = availH * ratio;
            var firstRect = new Rect(rect.X, rect.Y, rect.Width, firstH);
            var secondRect = new Rect(rect.X, rect.Y + firstH + thickness, rect.Width, availH - firstH);
            return Walk(s.First, firstRect, target, found) || Walk(s.Second, secondRect, target, found);
        }
    }

    /// <summary>あるスクリーン上座標がどの leaf 上にあるかを返す (D&D ドロップ判定で使う)。
    /// どの leaf にも当たらないなら null。</summary>
    public PaneId? HitTestLeafAt(Point pointInPanel)
    {
        foreach (var (paneId, rect) in _leafRects)
        {
            if (rect.Contains(pointInPanel)) return paneId;
        }
        return null;
    }

    /// <summary>指定 leaf の現在の配置 Rect を返す (= drop zone 表示の計算に使う)。</summary>
    public Rect? GetLeafRect(PaneId pane)
        => _leafRects.TryGetValue(pane, out var rect) ? rect : null;

    // ---- ペインドラッグ受信 (Mouse.Capture ベースの自前実装) ----
    //
    // OLE DragDrop (DragDrop.DoDragDrop + AllowDrop) は WebView2 (HwndHost / Win32 island) 上で
    // DragOver / Drop イベントが届かないという制約があり、「ヘッダ部分でしかドロップできない」状態に
    // なっていた。Mouse.Capture を使うと HwndHost 子 HWND を跨いでもマウスイベントが
    // capture 取得元 (= この Panel) に届くので、WebView2 上にもドロップできるようになる
    // (GestureRecognizer の WebView2 跨ぎ問題と同じ解決策)。

    // overlay は Popup ベース (= 別 HWND として常に最前面)。WPF の AdornerLayer だと HwndHost (= WebView2) の
    // 裏に隠れて 1 ドットしか見えない、という airspace 問題があるため。
    private Popup?                _overlayPopup;
    private PaneDropZoneOverlay?  _overlay;
    private bool                  _isPaneDragging;
    private PaneId                _paneDragSource;

    public PaneLayoutPanel()
    {
        // Background を設定しないと「子で覆われていない領域」 (= splitter のスキマ) で
        // hit-test が抜ける → splitter ドラッグの mouse-down を観測できない。
        // SystemColors.ControlBrush で塗っておけば splitter 部分が見え、かつ hit-test も効く。
        Background = SystemColors.ControlBrush;
    }

    /// <summary>ペインヘッダから「ドラッグ閾値超え」が起きたときに呼ぶ。Mouse.Capture をこの Panel に
    /// 取って drop adorner を表示し、以降の MouseMove / MouseUp は <see cref="OnPreviewMouseMove"/> /
    /// <see cref="OnPreviewMouseLeftButtonUp"/> がこの Panel で受ける。</summary>
    public void BeginPaneDrag(PaneId source)
    {
        if (_isPaneDragging) return;
        if (_layout is null) return;
        _isPaneDragging = true;
        _paneDragSource = source;
        EnsureOverlay();
        // CaptureMouse で WebView2 (HwndHost) を跨いでも MouseMove / MouseUp が取れる。
        CaptureMouse();
        // 初回 visualization (= 次の MouseMove を待たずに drop zone を出す)
        UpdateDragVisual(Mouse.GetPosition(this));
    }

    private void UpdateDragVisual(Point pointInPanel)
    {
        if (!_isPaneDragging) return;
        var target = HitTestLeafAt(pointInPanel);
        if (target is PaneId t && t != _paneDragSource && GetLeafRect(t) is Rect rect)
        {
            var side = ComputeDropSide(rect, pointInPanel);
            _overlay?.Update(ComputeZoneRect(rect, side));
        }
        else
        {
            _overlay?.Update(null);
        }
    }

    /// <summary>target leaf rect の中で <paramref name="side"/> 側半分を表す Rect を返す
    /// (= overlay でハイライト表示する領域)。</summary>
    private static Rect ComputeZoneRect(Rect r, DropSide side) => side switch
    {
        DropSide.Left   => new Rect(r.X, r.Y, r.Width / 2, r.Height),
        DropSide.Right  => new Rect(r.X + r.Width / 2, r.Y, r.Width / 2, r.Height),
        DropSide.Top    => new Rect(r.X, r.Y, r.Width, r.Height / 2),
        DropSide.Bottom => new Rect(r.X, r.Y + r.Height / 2, r.Width, r.Height / 2),
        _               => r,
    };

    /// <summary>マウス位置 (この Panel 座標) と target leaf rect から drop side を決める。
    /// 矩形の対角線で 4 つの三角形 wedge に分け、カーソルがどの wedge にいるかで判定する
    /// (= アスペクト比に依らず常に Left/Right/Top/Bottom が 25% ずつの面積を取る)。
    /// 旧実装の「最も近いエッジ」方式だと、横長ペインでは Top/Bottom がほぼ全域を占有し、
    /// Left/Right は端の細い帯にしかならない (= "1 ドットしかない" 問題) ため。</summary>
    private static DropSide ComputeDropSide(Rect rect, Point point)
    {
        var halfW = rect.Width  / 2;
        var halfH = rect.Height / 2;
        if (halfW <= 0 || halfH <= 0) return DropSide.Right;
        var nx = (point.X - (rect.X + halfW)) / halfW;
        var ny = (point.Y - (rect.Y + halfH)) / halfH;
        if (Math.Abs(nx) > Math.Abs(ny))
            return nx < 0 ? DropSide.Left : DropSide.Right;
        return ny < 0 ? DropSide.Top : DropSide.Bottom;
    }

    /// <summary>overlay 用 Popup + 中身を必要なら作って IsOpen=true にする。
    /// Popup は別 HWND で WPF Window 上に作られ、WebView2 (HwndHost) の上に確実に描画される。</summary>
    private void EnsureOverlay()
    {
        if (_overlayPopup is null)
        {
            _overlay = new PaneDropZoneOverlay();
            _overlayPopup = new Popup
            {
                Child                        = _overlay,
                PlacementTarget              = this,
                Placement                    = PlacementMode.Custom,
                CustomPopupPlacementCallback = (_, _, _) => new[]
                {
                    new CustomPopupPlacement(new Point(0, 0), PopupPrimaryAxis.None),
                },
                AllowsTransparency = true,
                StaysOpen          = true,
                IsHitTestVisible   = false, // popup 自体もマウスを奪わない (= 念のため)
            };
        }
        // panel サイズに追従 (= ドラッグ中にウィンドウサイズが変わっても overlay が伸縮する保険)
        if (_overlay is not null)
        {
            _overlay.Width  = ActualWidth;
            _overlay.Height = ActualHeight;
        }
        _overlayPopup.IsOpen = true;
    }

    private void RemoveOverlay()
    {
        if (_overlayPopup is null) return;
        _overlay?.Update(null);
        _overlayPopup.IsOpen = false;
    }

    /// <summary>カーソル位置で実際にドロップを試みる。有効な target が当たっていればレイアウトツリーを更新する。</summary>
    private void TryCommitPaneDrop(Point pointInPanel)
    {
        if (_layout is null) return;
        if (HitTestLeafAt(pointInPanel) is not PaneId target) return;
        if (target == _paneDragSource) return;
        if (GetLeafRect(target) is not Rect rect) return;
        var side = ComputeDropSide(rect, pointInPanel);

        var withoutSource = PaneLayoutOps.RemovePane(_layout, _paneDragSource);
        if (withoutSource is null) return;
        var newLayout = PaneLayoutOps.SplitAtLeaf(withoutSource, target, side, _paneDragSource);
        if (!newLayout.IsValidFullLayout()) return;
        _layout = newLayout;
        InvalidateMeasure();
        InvalidateArrange();
        RaiseLayoutChanged();
    }

    private void EndPaneDrag()
    {
        _isPaneDragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        RemoveOverlay();
    }
}
