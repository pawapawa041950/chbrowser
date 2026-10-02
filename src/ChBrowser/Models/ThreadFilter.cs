namespace ChBrowser.Models;

/// <summary>
/// スレッド表示にかけるフィルタ条件。
///
/// <para>用途: スレッド表示ペインのヘッダにあるテキストボックス (本文絞り込み) や、
/// 人気のレス (👍 のメニュー) / 画像レス (🖼) の絞り込みを 1 オブジェクトに集約して JS 側に push する。
/// 新しい条件を増やす場合はこの record にプロパティを追加し、push する JSON と JS 側の <c>setFilter</c> に足す。</para>
///
/// <para>cardinal: <see cref="IsEmpty"/> = true なら全レス可視 (= フィルタなし) を意味する。
/// <see cref="WebView2Helper"/> の FilterPush attached property は値変化のたびに JS に push し、
/// JS 側が DOM の visibility を更新する。</para>
/// </summary>
/// <param name="TextQuery">本文に対する部分一致クエリ (= 大文字小文字無視)。空なら本文条件なし。</param>
/// <param name="MinReplies">「人気のレス」: 返信数がこの値以上のレス (0 = 返信数の条件なし)。</param>
/// <param name="MinLikes">「人気のレス」: イイネ数 (reddit のスコア / ふたばの「そうだね」) がこの値以上のレス (0 = 条件なし)。
/// 返信数の条件と両方あるときは OR。tree / dedupTree モードでは人気のレスの配下 (= 返信チェイン) も含めて表示する。</param>
/// <param name="MediaOnly">画像 / 動画 URL を含むレスのみ表示。人気のレスの条件と同時のときは OR (= どれかに該当すれば表示)。</param>
public sealed record ThreadFilter(
    string TextQuery  = "",
    int    MinReplies = 0,
    int    MinLikes   = 0,
    bool   MediaOnly  = false)
{
    /// <summary>人気のレスの条件があるか。</summary>
    public bool PopularOn => MinReplies > 0 || MinLikes > 0;

    /// <summary>すべての条件が「指定なし」相当か。true なら JS 側はフィルタを切る (= 全レス可視)。</summary>
    public bool IsEmpty => string.IsNullOrEmpty(TextQuery) && !PopularOn && !MediaOnly;
}
