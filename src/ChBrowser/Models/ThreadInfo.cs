namespace ChBrowser.Models;

/// <summary>
/// スレ一覧 (subject.txt) の 1 エントリ。
/// 例: <c>1234567890.dat&lt;&gt;スレタイトル (123)</c>
/// </summary>
public sealed record ThreadInfo(
    string Key,        // 例: "1234567890" (dat ファイル名 = スレッド作成 epoch)
    string Title,      // 例: "スレタイトル"
    int    PostCount,  // 例: 123
    int    Order,      // subject.txt 上の出現順 (1 始まり)
    long?  CreatedEpoch = null,  // スレ作成時刻 (epoch 秒)。null なら key を epoch とみなす (5ch 系)。勢い計算に使う
    long?  Score        = null); // 提供者固有の評価値 (reddit の score)。無ければ null
