using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace ChBrowser.Controls;

/// <summary>ペインのヘッダ 1 行のレイアウト。子を左寄せのグループと右寄せのグループに並べ、
/// <b>幅は優先度の順に配る</b> (足りなければ優先度の低い子から縮む / 消える)。
///
/// <para>スレ一覧 / スレ表示ペインのヘッダでは: ボタン群 (0) → 掲示板名・板名 (1) → 検索欄 (2) → 板名・スレタイ (3)。
/// つまり幅が足りないときはまずタイトルの後ろ側が縮み、次に検索欄、最後にタイトルの前側。ボタンは常に出る。</para>
///
/// <list type="bullet">
/// <item><description><see cref="PriorityProperty"/>: 小さいほど先に幅をもらう。</description></item>
/// <item><description><see cref="AlignRightProperty"/>: true の子は並び順のまま右端に寄せて詰める (最後の子が右端)。false は左端から並び順に。</description></item>
/// <item><description><see cref="PreferredWidthProperty"/>: 中身が小さくてもこの幅まではもらう (検索欄を開いたときの幅)。NaN なら中身の幅。</description></item>
/// </list></summary>
public sealed class HeaderStripPanel : Panel
{
    public static readonly DependencyProperty PriorityProperty = DependencyProperty.RegisterAttached(
        "Priority", typeof(int), typeof(HeaderStripPanel),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsParentMeasure));
    public static int  GetPriority(UIElement e) => (int)e.GetValue(PriorityProperty);
    public static void SetPriority(UIElement e, int v) => e.SetValue(PriorityProperty, v);

    public static readonly DependencyProperty AlignRightProperty = DependencyProperty.RegisterAttached(
        "AlignRight", typeof(bool), typeof(HeaderStripPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentArrange));
    public static bool GetAlignRight(UIElement e) => (bool)e.GetValue(AlignRightProperty);
    public static void SetAlignRight(UIElement e, bool v) => e.SetValue(AlignRightProperty, v);

    public static readonly DependencyProperty PreferredWidthProperty = DependencyProperty.RegisterAttached(
        "PreferredWidth", typeof(double), typeof(HeaderStripPanel),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsParentMeasure));
    public static double GetPreferredWidth(UIElement e) => (double)e.GetValue(PreferredWidthProperty);
    public static void   SetPreferredWidth(UIElement e, double v) => e.SetValue(PreferredWidthProperty, v);

    /// <summary>直近の測定で各子に配った幅 (Arrange で使う)。</summary>
    private readonly Dictionary<UIElement, double> _widths = new();

    protected override Size MeasureOverride(Size available)
    {
        _widths.Clear();
        var remaining = double.IsInfinity(available.Width) ? double.PositiveInfinity : available.Width;
        var height    = 0.0;
        var children  = InternalChildren.Cast<UIElement>().Where(c => c is not null).ToList();
        foreach (var child in children.OrderBy(GetPriority))   // OrderBy は安定ソート = 同じ優先度は並び順
        {
            if (child.Visibility == Visibility.Collapsed) { child.Measure(new Size(0, 0)); _widths[child] = 0; continue; }
            child.Measure(new Size(Math.Max(0, remaining), available.Height));
            var want = child.DesiredSize.Width;
            var pref = GetPreferredWidth(child);
            if (!double.IsNaN(pref) && pref > want) want = pref;
            var take = Math.Max(0, Math.Min(want, remaining));
            if (take < child.DesiredSize.Width) child.Measure(new Size(take, available.Height));   // 縮めた幅で測り直す (文字の省略など)
            _widths[child] = take;
            remaining -= take;
            height = Math.Max(height, child.DesiredSize.Height);
        }
        var used = _widths.Values.Sum();
        return new Size(double.IsInfinity(available.Width) ? used : available.Width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var children = InternalChildren.Cast<UIElement>().Where(c => c is not null).ToList();
        var x = 0.0;
        foreach (var child in children.Where(c => !GetAlignRight(c)))
        {
            var w = _widths.TryGetValue(child, out var v) ? v : 0;
            child.Arrange(new Rect(x, 0, w, final.Height));
            x += w;
        }
        var right = children.Where(GetAlignRight).ToList();
        var r = final.Width - right.Sum(c => _widths.TryGetValue(c, out var v) ? v : 0);
        foreach (var child in right)
        {
            var w = _widths.TryGetValue(child, out var v) ? v : 0;
            child.Arrange(new Rect(r, 0, w, final.Height));
            r += w;
        }
        return final;
    }
}
