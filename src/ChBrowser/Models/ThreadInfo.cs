namespace ChBrowser.Models;

/// <summary>
/// スレ一覧 (subject.txt) の 1 エントリ。
/// 例: <c>1234567890.dat&lt;&gt;スレタイトル (123)</c>
/// </summary>
public sealed record ThreadInfo(
    string Key,        // 例: "1234567890" (dat ファイル名 = スレッド作成 epoch)
    string Title,      // 例: "スレタイトル"
    int    PostCount,  // 例: 123
    int    Order);     // subject.txt 上の出現順 (1 始まり)
