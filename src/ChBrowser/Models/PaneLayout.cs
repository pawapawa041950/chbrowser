using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace ChBrowser.Models;

/// <summary>ペインの「種類」。レイアウトツリーの leaf がこの値を持つ (Phase 23 / docking 化)。
/// 文字列値は <c>layout.json</c> のシリアライズ表現として固定 (rename 不可、互換のため)。
/// ログ表示は別ウィンドウ (= <see cref="ChBrowser.Views.LogWindow"/>) に分離されているのでここには無い。
///
/// <para>「種類」であって「インスタンス」ではない点に注意 (複数ペイン化, Phase 1〜)。同じ種類のペインを
/// 複数枚並べられるかどうかは <see cref="PaneKinds.IsSingleton"/> で種類ごとに決まる。複数枚許可の種類
/// (= ThreadDisplay、将来 ThreadList) は leaf に <see cref="LeafLayoutNode.InstanceId"/> を持たせて区別する。</para></summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PaneId
{
    Favorites,
    BoardList,
    ThreadList,
    ThreadDisplay,
}

/// <summary>ペイン種類の「単一 / 複数」能力を一元管理する (複数ペイン化, Phase 1〜)。
/// 将来 ThreadList を複数化するときは <see cref="IsSingleton"/> の判定を緩めるだけで土台が再利用できる。</summary>
public static class PaneKinds
{
    /// <summary>この種類は画面内に 1 枚しか置けない (= シングルトン) か。
    /// 現状 ThreadDisplay のみ複数可。それ以外 (お気に入り / 板 / スレ一覧) は各 1 枚。</summary>
    public static bool IsSingleton(PaneId kind) => kind != PaneId.ThreadDisplay;

    /// <summary>(kind, instanceId) から leaf / 子コントロールを一意に指す文字列キーを作る。
    /// シングルトン (instanceId 無し) は種類名そのもの ("ThreadList")、複数可インスタンスは "ThreadDisplay:&lt;id&gt;"。</summary>
    public static string MakeKey(PaneId kind, string? instanceId)
        => string.IsNullOrEmpty(instanceId) ? kind.ToString() : $"{kind}:{instanceId}";

    /// <summary><see cref="MakeKey"/> の逆。"ThreadDisplay:&lt;id&gt;" → (ThreadDisplay, id)、
    /// "ThreadList" → (ThreadList, null)。種類名が未知なら false。</summary>
    public static bool TryParseKey(string key, out PaneId kind, out string? instanceId)
    {
        instanceId = null;
        kind       = default;
        if (string.IsNullOrEmpty(key)) return false;
        var idx      = key.IndexOf(':');
        var kindPart = idx < 0 ? key : key.Substring(0, idx);
        if (!Enum.TryParse(kindPart, out kind)) return false;
        if (idx >= 0) instanceId = key.Substring(idx + 1);
        return true;
    }
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

    /// <summary>ツリーの整合性検証 (複数ペイン化対応, Phase 1〜):
    /// シングルトン種 (お気に入り / 板 / スレ一覧) は各ちょうど 1 枚、複数可種 (ThreadDisplay) は 1 枚以上、
    /// かつ全 leaf の <see cref="LeafLayoutNode.Key"/> が一意であること。
    /// 不正レイアウト (永続化破損 / 編集ミス) を検出した呼出元はデフォルトレイアウトに fallback すべき。</summary>
    public bool IsValidFullLayout()
    {
        var leaves = EnumerateLeaves().ToList();
        if (leaves.Count == 0) return false;

        // 種類ごとの枚数を数える。
        var counts = new Dictionary<PaneId, int>();
        foreach (var l in leaves)
            counts[l.Pane] = counts.TryGetValue(l.Pane, out var c) ? c + 1 : 1;

        // シングルトン種はちょうど 1 枚。
        foreach (var kind in new[] { PaneId.Favorites, PaneId.BoardList, PaneId.ThreadList })
            if (counts.GetValueOrDefault(kind) != 1) return false;
        // 複数可種 (ThreadDisplay) は最低 1 枚。
        if (counts.GetValueOrDefault(PaneId.ThreadDisplay) < 1) return false;

        // インスタンスキーの重複が無いこと (= 同じ ThreadDisplay インスタンスが 2 leaf に出ない)。
        var keys = leaves.Select(l => l.Key).ToList();
        return keys.Distinct().Count() == keys.Count;
    }

    /// <summary>クローン (永続化往復に使う / 編集前のスナップショット用)。</summary>
    public abstract LayoutNode Clone();
}

/// <summary>ツリーの leaf — 1 ペイン分の表示位置を表す。
/// <see cref="Pane"/> は種類、<see cref="InstanceId"/> は複数可種 (ThreadDisplay) のインスタンス識別子
/// (シングルトン種では null)。両者の組が <see cref="Key"/> でレイアウトエンジン / 永続化のキーになる。</summary>
public sealed class LeafLayoutNode : LayoutNode
{
    public PaneId Pane { get; set; }

    /// <summary>複数可種のインスタンス識別子。シングルトン種では null。
    /// 旧 layout.json には存在しないが、null 既定なので後方互換で読める。</summary>
    public string? InstanceId { get; set; }

    /// <summary>レイアウトエンジン (rect 辞書 / hit-test) と永続化で使う一意キー。</summary>
    [JsonIgnore]
    public string Key => PaneKinds.MakeKey(Pane, InstanceId);

    public LeafLayoutNode() { }
    public LeafLayoutNode(PaneId pane, string? instanceId = null) { Pane = pane; InstanceId = instanceId; }

    public override LayoutNode Clone() => new LeafLayoutNode(Pane, InstanceId);
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

    /// <summary>指定キーの leaf を取り除き、ツリーを縮める (sibling が親 split を置き換える)。
    /// 取り除いた結果の root を返す。元 root が唯一の leaf でそれ自身が対象なら null を返す
    /// (= 最後の 1 枚を消すケース。呼出側が「最低 1 枚維持」をガードする責務)。</summary>
    public static LayoutNode RemoveLeaf(LayoutNode root, string key)
    {
        if (root is LeafLayoutNode leaf) return leaf.Key == key ? null! : leaf;
        if (root is not SplitLayoutNode split) return root;

        // 直接子が対象 leaf なら、もう一方の子で split 自体を置換
        if (split.First  is LeafLayoutNode lf && lf.Key == key) return split.Second;
        if (split.Second is LeafLayoutNode lr && lr.Key == key) return split.First;

        // 子が split node なら再帰
        split.First  = RemoveLeaf(split.First,  key);
        split.Second = RemoveLeaf(split.Second, key);
        return split;
    }

    /// <summary>キー <paramref name="targetKey"/> の leaf を新しい split に置換する。
    /// <paramref name="dropSide"/> = <paramref name="newLeaf"/> を target のどちら側に入れるか (上 / 下 / 左 / 右)。
    /// target は <paramref name="newLeaf"/> と組合わせた split node に変身する。</summary>
    public static LayoutNode SplitAtLeaf(LayoutNode root, string targetKey, DropSide dropSide, LeafLayoutNode newLeaf)
    {
        return Replace(root, targetKey, leaf =>
        {
            var orientation = (dropSide == DropSide.Left || dropSide == DropSide.Right)
                ? LayoutOrientation.Horizontal
                : LayoutOrientation.Vertical;
            // dropSide が Left / Top なら新 leaf が First (前側)、Right / Bottom なら Second (後側)
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

    /// <summary>ツリー走査でキー <paramref name="targetKey"/> の leaf を見つけ、
    /// <paramref name="transform"/> でその leaf を新ノードに変換する。target が見つからなければ root をそのまま返す。</summary>
    private static LayoutNode Replace(LayoutNode node, string targetKey, Func<LeafLayoutNode, LayoutNode> transform)
    {
        if (node is LeafLayoutNode leaf)
            return leaf.Key == targetKey ? transform(leaf) : leaf;
        if (node is SplitLayoutNode split)
        {
            split.First  = Replace(split.First,  targetKey, transform);
            split.Second = Replace(split.Second, targetKey, transform);
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
