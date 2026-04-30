namespace ChBrowser.Models;

/// <summary>
/// スレッドごとの読了/位置情報。idx.json として板ディレクトリに永続化される。
/// </summary>
/// <param name="LastReadPostNumber">最後にスクロール上端にあったレス番号 (位置復元用)。</param>
/// <param name="LastFetchedPostCount">前回取得完了時のレス総数。subject.txt の件数と比較して新着の有無を判定する。</param>
public sealed record ThreadIndex(int? LastReadPostNumber, int? LastFetchedPostCount);
