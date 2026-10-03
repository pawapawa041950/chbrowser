namespace ChBrowser.Models;

/// <summary>
/// スレッド表示にかけるフィルタ条件。
///
/// <para>用途: スレッド表示ペインのヘッダにあるテキストボックス (本文絞り込み) や、
/// フィルターボタンのメニュー (返信数 / イイネ数 / 画像・動画) の絞り込みを 1 オブジェクトに集約して JS 側に push する。
/// 新しい条件を増やす場合はこの record にプロパティを追加し、push する JSON と JS 側の <c>setFilter</c> に足す。</para>
///
/// <para>cardinal: <see cref="IsEmpty"/> = true なら全レス可視 (= フィルタなし) を意味する。
/// <see cref="WebView2Helper"/> の FilterPush attached property は値変化のたびに JS に push し、
/// JS 側が DOM の visibility を更新する。</para>
/// </summary>
/// <param name="TextQuery">本文に対する部分一致クエリ (= 大文字小文字無視)。空なら本文条件なし。</param>
/// <param name="MinReplies">返信数がこの値以上のレス (0 = 返信数の条件なし)。</param>
/// <param name="MinLikes">イイネ数 (reddit のスコア / ふたばの「そうだね」) がこの値以上のレス (0 = 条件なし)。</param>
/// <param name="MediaOnly">画像 / 動画 URL を含むレス。</param>
/// <param name="MatchAll">返信数 / イイネ数 / 画像・動画のうち使っている条件の組み合わせ。false = どれかを満たせば表示 (OR)、true = すべて満たすものだけ (AND)。
/// tree / dedupTree モードでは、返信数かイイネ数の条件があるとき、条件に合うレスの配下 (= 返信チェイン) も含めて表示する。</param>
public sealed record ThreadFilter(
    string TextQuery  = "",
    int    MinReplies = 0,
    int    MinLikes   = 0,
    bool   MediaOnly  = false,
    bool   MatchAll   = false)
{
    /// <summary>人気のレスの条件があるか。</summary>
    public bool PopularOn => MinReplies > 0 || MinLikes > 0;

    /// <summary>すべての条件が「指定なし」相当か。true なら JS 側はフィルタを切る (= 全レス可視)。</summary>
    public bool IsEmpty => string.IsNullOrEmpty(TextQuery) && !PopularOn && !MediaOnly;
}
