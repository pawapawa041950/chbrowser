namespace ChBrowser.Models;

/// <summary>本文中の「レス参照 (アンカー)」を判定する規則 1 件。掲示板ごとに規則の並びを持つ
/// (<c>doc/multi-bbs-design.md</c> §6)。表示は元の文字列のまま、判定だけをこの規則で行う。
///
/// <list type="bullet">
/// <item><description><see cref="Pattern"/>: 正規表現ソース。対象を取り出す名前付きグループ <c>(?&lt;spec&gt;...)</c> が必須。
///   C# と JS の両方で同じ式を使うため、両者に共通の構文に限定する (名前付きグループ・文字クラス・量指定子・先読み。後読みは使わない)。</description></item>
/// <item><description><see cref="Kind"/>: <c>"number"</c> = spec がそのままレス番号 (範囲・カンマ指定は <see cref="Ranges"/> 次第)、
///   <c>"attachment"</c> = spec は添付ファイル名で、そのファイルを添付したレスの番号に解決する (ふたばの <c>&gt;1789890966670.png</c>)。</description></item>
/// <item><description><see cref="Ranges"/>: <c>&gt;&gt;3-5,7</c> のような範囲・カンマ指定を許すか。</description></item>
/// </list></summary>
public sealed record AnchorRule(
    string Name,
    string Pattern,
    string Kind    = AnchorRule.KindNumber,
    bool   Ranges  = true,
    bool   Enabled = true)
{
    public const string KindNumber     = "number";
    public const string KindAttachment = "attachment";

    public bool IsAttachment => string.Equals(Kind, KindAttachment, System.StringComparison.OrdinalIgnoreCase);
}
