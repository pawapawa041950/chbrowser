using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace ChBrowser.Models;

/// <summary>4 ペインの識別子。レイアウトツリーの leaf がこの値を持つ (Phase 23 / docking 化)。
/// 文字列値は <c>layout.json</c> のシリアライズ表現として固定 (rename 不可、互換のため)。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PaneId
{
    Favorites,
    BoardList,
    ThreadList,
    ThreadDisplay,
}

/// <summary>SplitNode の分割方向。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LayoutOrientation
{
    /// <summary>水平方向に分割 (= 左右 2 分割、子は左右に並ぶ)。</summary>
    Horizontal,
    /// <summary>垂直方向に分割 (= 上下 2 分割、子は上下に並ぶ)。</summary>
    Vertical,
}

/// <summary>レイアウトツリーのノード。leaf or split の 2 種類。
/// JSON 永続化のため <c>$type</c> 識別子で polymorphic シリアライズする。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(LeafLayoutNode),  typeDiscriminator: "leaf")]
[JsonDerivedType(typeof(SplitLayoutNode), typeDiscriminator: "split")]
public abstract class LayoutNode
{
    /// <summary>このノード以下の全 leaf を visit する (DFS)。</summary>
    public IEnumerable<LeafLayoutNode> EnumerateLeaves()
    {
        if (this is LeafLayoutNode l) yield return l;
        else if (this is SplitLayoutNode s)
        {
            foreach (var x in s.First.EnumerateLeaves())  yield return x;
            foreach (var x in s.Second.EnumerateLeaves()) yield return x;
        }
    }

    /// <summary>ツリーの整合性検証: 4 ペインすべてが過不足なく出現するか。
    /// 不正レイアウト (永続化破損 / 編集ミス) を検出した呼出元はデフォルトレイアウトに fallback すべき。</summary>
    public bool IsValidFullLayout()
    {
        var leaves = EnumerateLeaves().Select(l => l.Pane).ToList();
        if (leaves.Count != 4) return false;
        var distinct = new HashSet<PaneId>(leaves);
        return distinct.Count == 4;
    }

    /// <summary>クローン (永続化往復に使う / 編集前のスナップショット用)。</summary>
    public abstract LayoutNode Clone();
}

/// <summary>ツリーの leaf — 1 ペイン分の表示位置を表す。</summary>
public sealed class LeafLayoutNode : LayoutNode
{
    public PaneId Pane { get; set; }

    public LeafLayoutNode() { }
    public LeafLayoutNode(PaneId pane) { Pane = pane; }

    public override LayoutNode Clone() => new LeafLayoutNode(Pane);
}

/// <summary>ツリーの内部ノード — 2 つの子を <see cref="Orientation"/> 方向に <see cref="Ratio"/> の比率で並べる。
/// Ratio は First の占める割合 (0.0 〜 1.0)、Second は (1 - Ratio)。</summary>
public sealed class SplitLayoutNode : LayoutNode
{
    public LayoutOrientation Orientation { get; set; }
    public double            Ratio       { get; set; } = 0.5;
    public LayoutNode        First       { get; set; } = default!;
    public LayoutNode        Second      { get; set; } = default!;

    public SplitLayoutNode() { }
    public SplitLayoutNode(LayoutOrientation orientation, double ratio, LayoutNode first, LayoutNode second)
    {
        Orientation = orientation;
        Ratio       = Math.Clamp(ratio, 0.05, 0.95);
        First       = first;
        Second      = second;
    }

    public override LayoutNode Clone() => new SplitLayoutNode(Orientation, Ratio, First.Clone(), Second.Clone());
}

/// <summary>レイアウト操作の static ヘルパ集。
/// レイアウトツリーは immutable に扱わず、直接ミューテーションするが、変更時には <see cref="LayoutPaneHost.OnLayoutChanged"/>
/// 経由で再描画 + 永続化トリガをかける流儀 (= UI 側で必ず Notify する責務)。</summary>
public static class PaneLayoutOps
{
    /// <summary>初期レイアウト: 旧 3 列構成と同じ見た目になる組合せ。
    /// <code>
    ///   ┌──────────┬────────────────┐
    ///   │ Favorites│ ThreadList     │
    ///   │ ─────────│ ───────────────│
    ///   │ BoardList│ ThreadDisplay  │
    ///   └──────────┴────────────────┘
    /// </code></summary>
    public static LayoutNode BuildDefault()
        => new SplitLayoutNode(
            LayoutOrientation.Horizontal,
            ratio: 0.20,
            first: new SplitLayoutNode(
                LayoutOrientation.Vertical,
                ratio: 0.40,
                first:  new LeafLayoutNode(PaneId.Favorites),
                second: new LeafLayoutNode(PaneId.BoardList)),
            second: new SplitLayoutNode(
                LayoutOrientation.Vertical,
                ratio: 0.40,
                first:  new LeafLayoutNode(PaneId.ThreadList),
                second: new LeafLayoutNode(PaneId.ThreadDisplay)));

    /// <summary>指定 pane を含む leaf を取り除き、ツリーを縮める (sibling が親 split を置き換える)。
    /// 取り除いた結果の root を返す。元 root が leaf (= 1 個しか pane が無いケース) なら null を返すが、
    /// 4 pane システムではこのケースは起きない (= drop 時に必ず 4 leaf が維持される)。</summary>
    public static LayoutNode RemovePane(LayoutNode root, PaneId pane)
    {
        if (root is LeafLayoutNode leaf) return leaf.Pane == pane ? null! : leaf;
        if (root is not SplitLayoutNode split) return root;

        // 子に含まれる pane を再帰的に削る
        // 直接子が leaf でターゲットに一致するなら、もう一方の子で split 自体を置換
        if (split.First  is LeafLayoutNode lf && lf.Pane == pane) return split.Second;
        if (split.Second is LeafLayoutNode lr && lr.Pane == pane) return split.First;

        // 子が split node なら再帰
        split.First  = RemovePane(split.First,  pane);
        split.Second = RemovePane(split.Second, pane);
        return split;
    }

    /// <summary>指定 target leaf を新しい split に置換する。
    /// <paramref name="dropSide"/> = どこに新 pane を入れるか (上 / 下 / 左 / 右)。
    /// target は <paramref name="newPane"/> と組合わせた split node に変身する。</summary>
    public static LayoutNode SplitAtLeaf(LayoutNode root, PaneId targetPane, DropSide dropSide, PaneId newPane)
    {
        return Replace(root, targetPane, leaf =>
        {
            var orientation = (dropSide == DropSide.Left || dropSide == DropSide.Right)
                ? LayoutOrientation.Horizontal
                : LayoutOrientation.Vertical;
            // dropSide が Left / Top なら新 pane が First (前側)、Right / Bottom なら Second (後側)
            var newLeaf = new LeafLayoutNode(newPane);
            LayoutNode first, second;
            if (dropSide == DropSide.Left || dropSide == DropSide.Top)
            {
                first  = newLeaf;
                second = leaf;
            }
            else
            {
                first  = leaf;
                second = newLeaf;
            }
            return new SplitLayoutNode(orientation, ratio: 0.5, first, second);
        });
    }

    /// <summary>ツリー走査で <paramref name="targetPane"/> を含む leaf を見つけ、
    /// <paramref name="transform"/> でその leaf を新ノードに変換する。target が見つからなければ root をそのまま返す。</summary>
    private static LayoutNode Replace(LayoutNode node, PaneId targetPane, Func<LeafLayoutNode, LayoutNode> transform)
    {
        if (node is LeafLayoutNode leaf)
            return leaf.Pane == targetPane ? transform(leaf) : leaf;
        if (node is SplitLayoutNode split)
        {
            split.First  = Replace(split.First,  targetPane, transform);
            split.Second = Replace(split.Second, targetPane, transform);
        }
        return node;
    }
}

/// <summary>ペインドロップ時、target ペインのどの辺にソースを入れるか。</summary>
public enum DropSide
{
    Left,
    Right,
    Top,
    Bottom,
}
