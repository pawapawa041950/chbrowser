using ChBrowser.ViewModels;

namespace ChBrowser.Models;

/// <summary>スレ一覧 1 行の種別。既定は <see cref="Thread"/> (= 従来通り)。
/// <see cref="Board"/> は「板一覧以外の取得済み板」集約タブのように行が板そのものを表す場合
/// (= クリックで板を開く、No / 勢い は空、数 = ローカル dat 件数)。</summary>
public enum ThreadListItemKind
{
    Thread,
    Board,
}

/// <summary>
/// スレ一覧 1 行分のレンダリング材料。
/// 通常の板表示では同じ板から並ぶだけだが、お気に入りディレクトリ表示では行ごとに異なる板から
/// 来たスレが混じるため、行単位で板情報 (host, directoryName, boardName) を持たせる。
/// JS の dblclick → host/dir/key/title を C# に投げる経路で必要。
///
/// <see cref="Kind"/>=<see cref="ThreadListItemKind.Board"/> の行では <see cref="ThreadInfo.Title"/> が板名、
/// <see cref="ThreadInfo.PostCount"/> がローカル dat 件数、<see cref="BoardName"/> が「提供者名 host」表示文字列。
/// </summary>
public sealed record ThreadListItem(
    ThreadInfo          Info,
    string              Host,
    string              DirectoryName,
    string              BoardName,
    LogMarkState        State,
    bool                IsFavorited = false,
    ThreadListItemKind  Kind        = ThreadListItemKind.Thread);
