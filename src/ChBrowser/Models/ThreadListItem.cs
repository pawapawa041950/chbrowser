using ChBrowser.ViewModels;

namespace ChBrowser.Models;

/// <summary>
/// スレ一覧 1 行分のレンダリング材料。
/// 通常の板表示では同じ板から並ぶだけだが、お気に入りディレクトリ表示では行ごとに異なる板から
/// 来たスレが混じるため、行単位で板情報 (host, directoryName, boardName) を持たせる。
/// JS の dblclick → host/dir/key/title を C# に投げる経路で必要。
/// </summary>
public sealed record ThreadListItem(
    ThreadInfo    Info,
    string        Host,
    string        DirectoryName,
    string        BoardName,
    LogMarkState  State,
    bool          IsFavorited = false);
