namespace ChBrowser.Models;

/// <summary>
/// スレッド表示にかけるフィルタ条件。
///
/// <para>用途: スレッド表示ペインのヘッダにあるテキストボックス (本文絞り込み) や、
/// 将来追加予定の各種フィルタ (返信数しきい値 / 自分のレスのみ / etc.) を 1 オブジェクトに集約して
/// JS 側に push する。新しい条件を増やす場合はこの record にプロパティを追加するだけでよく、
/// JSON シリアライズと JS 側の <c>setFilter</c> ハンドラがプロパティを自動的に受け取る。</para>
///
/// <para>cardinal: <see cref="IsEmpty"/> = true なら全レス可視 (= フィルタなし) を意味する。
/// <see cref="WebView2Helper"/> の FilterPush attached property は値変化のたびに JS に push し、
/// JS 側が DOM の visibility を更新する。</para>
/// </summary>
/// <param name="TextQuery">本文に対する部分一致クエリ (= 大文字小文字無視)。空なら本文条件なし。</param>
/// <param name="PopularOnly">「人気のレス」絞り込み (= 返信数が POPULAR_THRESHOLD 以上のレスのみ表示)。
/// tree / dedupTree モードでは popular レスの配下 (= 返信チェイン) も含めて表示する。
/// <see cref="MediaOnly"/> と同時 ON のときは OR (= どちらかに該当すれば表示)。</param>
/// <param name="MediaOnly">画像 / 動画 URL を含むレスのみ表示。<see cref="PopularOnly"/> と同時 ON のときは OR。</param>
public sealed record ThreadFilter(
    string TextQuery   = "",
    bool   PopularOnly = false,
    bool   MediaOnly   = false)
{
    /// <summary>すべての条件が「指定なし」相当か。true なら JS 側はフィルタを切る (= 全レス可視)。</summary>
    public bool IsEmpty => string.IsNullOrEmpty(TextQuery) && !PopularOnly && !MediaOnly;
}
