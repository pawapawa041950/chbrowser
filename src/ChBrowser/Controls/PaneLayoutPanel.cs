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

    /// <summary>複数可種 (ThreadDisplay) の動的生成ペインが「自分はどのインスタンスか」を表明する添付プロパティ。
    /// シングルトン種 / 静的ペインでは未設定 (= null)。<see cref="PaneId"/> と組で leaf キーを成す。</summary>
    public static readonly DependencyProperty InstanceIdProperty =
        DependencyProperty.RegisterAttached(
            "InstanceId",
            typeof(string),
            typeof(PaneLayoutPanel),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static string? GetInstanceId(DependencyObject d) => (string?)d.GetValue(InstanceIdProperty);
    public static void    SetInstanceId(DependencyObject d, string? value) => d.SetValue(InstanceIdProperty, value);

    /// <summary>子コントロールの leaf キー (= PaneId + InstanceId)。PaneId 未設定なら null。</summary>
    private static string? GetChildKey(DependencyObject d)
        => GetPaneId(d) is PaneId pid ? PaneKinds.MakeKey(pid, GetInstanceId(d)) : null;

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

    /// <summary>レイアウトツリーを差し替えて強制的に再配置する (複数ペイン化, Phase 3)。
    /// <see cref="PaneLayoutOps.SplitAtLeaf"/> / <see cref="PaneLayoutOps.RemoveLeaf"/> はツリーを in-place 改変して
    /// 同じ root 参照を返すことがあり、その場合 <see cref="Layout"/> セッターの参照比較で再配置がスキップされる。
    /// 本メソッドは参照に関係なく必ず measure/arrange を無効化し、変更通知も出す。</summary>
    public void ReplaceLayout(LayoutNode? layout)
    {
        _layout = layout;
        InvalidateMeasure();
        InvalidateArrange();
        RaiseLayoutChanged();
    }

    /// <summary>スプリッタの hit-test 太さ (px)。実際の splitter 視覚要素はこれと同じ太さで描画する。</summary>
    public double SplitterThickness { get; set; } = 4.0;

    /// <summary>各 leaf の最小サイズ (px)。リサイズで Ratio が更新されるとき、これを下回らないよう clamp する。</summary>
    public double MinPaneSize { get; set; } = 80.0;

    /// <summary>レイアウトが変化 (drag による ratio 変化 等) したときに上位へ通知するためのイベント。
    /// 永続化トリガに使う。</summary>
    public event EventHandler? LayoutChanged;

    private void RaiseLayoutChanged() => LayoutChanged?.Invoke(this, EventArgs.Empty);

    // ---- 各 leaf の配置領域を保持 (再描画間で再利用しない、毎回 ArrangeOverride で再計算) ----
    // キーは leaf の一意キー (= PaneKinds.MakeKey: "ThreadList" / "ThreadDisplay:<id>")。
    private readonly Dictionary<string, Rect>             _leafRects     = new();
    private readonly List<(SplitLayoutNode Node, Rect Bounds, bool IsHorizontal)> _splitterRects = new();

    // ---- ドラッグ中のスプリッタ ----
    private SplitLayoutNode? _draggingSplitter;
    private Rect             _draggingContainerRect;
    private bool             _draggingHorizontal;
    private Point            _dragStartPoint;
    private double           _dragStartRatio;

    protected override Size MeasureOverride(Size availableSize)
    {
        // 各ペインに「そのペインが arrange 時に占有する幅」を計測時にも渡す。
        // ウィンドウ全幅をそのまま渡すと、ペイン内部で WrapPanel 系の wrap 判定が
        // 「まだ余裕ある」と誤認して折り返しが発生しなくなるため。
        var size = ToFinite(availableSize);
        RecomputeLayoutRects(size);
        foreach (UIElement child in InternalChildren)
        {
            var rect = TryGetLeafRect(child);
            child.Measure(rect.HasValue ? new Size(rect.Value.Width, rect.Value.Height) : new Size(0, 0));
        }
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        RecomputeLayoutRects(finalSize);
        foreach (UIElement child in InternalChildren)
        {
            var rect = TryGetLeafRect(child);
            // PaneId 未割当 / レイアウト不在の子は (0,0,0,0) に潰して非表示扱い。
            child.Arrange(rect ?? new Rect(0, 0, 0, 0));
        }
        return finalSize;
    }

    /// <summary>サイズ (Measure / Arrange のいずれか) を受け取って _leafRects と _splitterRects を再計算する。
    /// 共通ロジック (旧実装で 2 メソッドに重複していた)。</summary>
    private void RecomputeLayoutRects(Size size)
    {
        _leafRects.Clear();
        _splitterRects.Clear();
        if (_layout is not null)
            ComputeLayout(_layout, new Rect(0, 0, size.Width, size.Height));
    }

    /// <summary>子の leaf キーに対応する矩形を返す (= 未割当なら null)。</summary>
    private Rect? TryGetLeafRect(UIElement child)
        => GetChildKey(child) is string key && _leafRects.TryGetValue(key, out var rect) ? rect : null;

    /// <summary>infinity 成分を 0 にした Size を返す (= MeasureOverride で渡される無限大対策)。</summary>
    private static Size ToFinite(Size s) => new(
        double.IsInfinity(s.Width)  ? 0 : s.Width,
        double.IsInfinity(s.Height) ? 0 : s.Height);

    /// <summary>レイアウトツリーを再帰的に走査して各 leaf の配置 Rect を <see cref="_leafRects"/> に登録、
    /// split 境界に対応する hit-test 用 Rect を <see cref="_splitterRects"/> に登録する。</summary>
    private void ComputeLayout(LayoutNode node, Rect rect)
    {
        if (node is LeafLayoutNode leaf)
        {
            _leafRects[leaf.Key] = rect;
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

        // 外部 (タブ) ドラッグ中: 位置だけ通知してホスト (MainWindow) に判定/描画を委ねる。
        if (_isExternalDragging)
        {
            if (e.LeftButton != MouseButtonState.Pressed) { EndExternalDrag(); return; }
            ExternalDragMove?.Invoke(e.GetPosition(this));
            e.Handled = true;
            return;
        }

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
        if (_isExternalDragging)
        {
            ExternalDragCommit?.Invoke(e.GetPosition(this));
            EndExternalDrag();
            e.Handled = true;
            return;
        }
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
        if (_isExternalDragging)  EndExternalDrag();
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

    /// <summary>あるスクリーン上座標がどの leaf 上にあるかを leaf キーで返す (D&D ドロップ判定で使う)。
    /// どの leaf にも当たらないなら null。</summary>
    public string? HitTestLeafAt(Point pointInPanel)
    {
        foreach (var (key, rect) in _leafRects)
        {
            if (rect.Contains(pointInPanel)) return key;
        }
        return null;
    }

    /// <summary>指定 leaf キーの現在の配置 Rect を返す (= drop zone 表示の計算に使う)。</summary>
    public Rect? GetLeafRect(string key)
        => _leafRects.TryGetValue(key, out var rect) ? rect : null;

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
    private string?               _paneDragSourceKey;

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
    public void BeginPaneDrag(string sourceKey)
    {
        if (_isPaneDragging) return;
        if (_layout is null) return;
        if (string.IsNullOrEmpty(sourceKey)) return;
        _isPaneDragging    = true;
        _paneDragSourceKey = sourceKey;
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
        if (target is string t && t != _paneDragSourceKey && GetLeafRect(t) is Rect rect)
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
        if (_paneDragSourceKey is not string sourceKey) return;
        if (HitTestLeafAt(pointInPanel) is not string target) return;
        if (target == sourceKey) return;
        if (GetLeafRect(target) is not Rect rect) return;
        if (!PaneKinds.TryParseKey(sourceKey, out var srcKind, out var srcInstance)) return;
        var side = ComputeDropSide(rect, pointInPanel);

        // ペイン「移動」: 同じ (kind, instanceId) の leaf を抜いて、target の side 側へ挿し直す。
        var withoutSource = PaneLayoutOps.RemoveLeaf(_layout, sourceKey);
        if (withoutSource is null) return;
        var movedLeaf = new LeafLayoutNode(srcKind, srcInstance);
        var newLayout = PaneLayoutOps.SplitAtLeaf(withoutSource, target, side, movedLeaf);
        if (!newLayout.IsValidFullLayout()) return;
        _layout = newLayout;
        InvalidateMeasure();
        InvalidateArrange();
        RaiseLayoutChanged();
    }

    private void EndPaneDrag()
    {
        _isPaneDragging    = false;
        _paneDragSourceKey = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        RemoveOverlay();
    }

    // ---- 外部ドラッグ (= タブ D&D) のための汎用キャプチャ機構 (複数ペイン化, Phase 3) ----
    //
    // ペイン本体へのドロップ (= 新ペイン生成) はオーバーレイを WebView2 (HwndHost) の上に出す必要があり、
    // それは既存のペイン移動と同じ Popup ベースのオーバーレイ + Mouse.Capture でしか実現できない。
    // ただし「どのペイン/タブストリップ上か」「挿入位置」「タブの移動/ペイン生成」という判断は VM/View 側
    // (MainWindow) の責務なので、ここは「キャプチャ + 位置通知 + ペイン本体オーバーレイ描画」だけを提供し、
    // 実際の処理はイベントでホストに委ねる (= この Panel は VM/タブストリップを知らないでいられる)。

    private bool _isExternalDragging;

    /// <summary>外部ドラッグ中のマウス移動 (引数はこの Panel 座標)。</summary>
    public event System.Action<Point>? ExternalDragMove;
    /// <summary>外部ドラッグのドロップ確定 (引数はこの Panel 座標)。<see cref="ExternalDragEnd"/> より前に発火。</summary>
    public event System.Action<Point>? ExternalDragCommit;
    /// <summary>外部ドラッグ終了 (ドロップ / キャンセル / キャプチャ喪失いずれでも)。後始末用。</summary>
    public event System.Action? ExternalDragEnd;

    /// <summary>外部 (タブ) ドラッグを開始する。Mouse.Capture をこの Panel に取り、以降の移動/離しを
    /// <see cref="ExternalDragMove"/> / <see cref="ExternalDragCommit"/> で通知する。</summary>
    public void BeginExternalDrag()
    {
        if (_isExternalDragging || _isPaneDragging) return;
        if (_layout is null) return;
        _isExternalDragging = true;
        EnsureOverlay();
        _overlay?.Update(null);
        CaptureMouse();
    }

    /// <summary>ペイン本体ドロップ先のハイライト矩形を更新する (null で消去)。タブストリップ上では null を渡す。</summary>
    public void ShowExternalDropZone(Rect? zone) => _overlay?.Update(zone);

    private void EndExternalDrag()
    {
        if (!_isExternalDragging) return;
        _isExternalDragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        RemoveOverlay();
        ExternalDragEnd?.Invoke();
    }

    /// <summary>指定座標が乗っている leaf について、ドロップ side とハイライト矩形を計算して返す
    /// (= ペイン本体ドロップ = 新ペイン生成のプレビュー用)。どの leaf にも当たらなければ null。</summary>
    public (string LeafKey, DropSide Side, Rect Zone)? ComputeBodyDropZone(Point pointInPanel)
    {
        if (HitTestLeafAt(pointInPanel) is not string key) return null;
        if (GetLeafRect(key) is not Rect rect) return null;
        var side = ComputeDropSide(rect, pointInPanel);
        return (key, side, ComputeZoneRect(rect, side));
    }
}
