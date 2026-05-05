namespace ChBrowser.Models;

/// <summary>
/// スレッドごとの読了/位置情報。idx.json として板ディレクトリに永続化される。
/// </summary>
/// <param name="LastReadPostNumber">最後にスクロール上端にあったレス番号 (位置復元用)。</param>
/// <param name="LastFetchedPostCount">前回取得完了時のレス総数。subject.txt の件数と比較して新着の有無を判定する。</param>
/// <param name="LastReadMarkPostNumber">「ここまで読んだ」帯の対象レス番号 (Phase 19)。
/// 「rect.bottom &lt;= viewport bottom」を満たす primary レス (= ツリーの子としての描画は除外) の
/// 最大番号を JS が追跡し、増加時のみ C# に通知して更新する (= 値は減少しない)。
/// dat 削除で idx.json ごと消えるため自動でリセットされる。</param>
/// <param name="OwnPostNumbers">「自分の書き込み」としてマークされているレス番号集合。
/// レス番号メニュー (post.html の data-number 経由) からトグルできる。null は空集合と同義。
/// dat 削除で idx.json ごと消えるため自動リセットされる。</param>
public sealed record ThreadIndex(
    int?   LastReadPostNumber,
    int?   LastFetchedPostCount,
    int?   LastReadMarkPostNumber = null,
    int[]? OwnPostNumbers          = null);
