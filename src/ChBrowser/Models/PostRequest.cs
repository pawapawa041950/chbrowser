namespace ChBrowser.Models;

/// <summary>
/// bbs.cgi に送る投稿リクエスト 1 件分。
/// レス書き込みは <see cref="ThreadKey"/> を、スレ立ては <see cref="Subject"/> を持つ
/// (両方持つことは無い想定だが、検証は <see cref="Services.Api.PostClient"/> 側で行う)。
///
/// 設計書 §3.4 に対応するフィールド:
///   bbs      = <see cref="Board"/>.DirectoryName (board.directoryName)
///   key      = <see cref="ThreadKey"/>
///   subject  = <see cref="Subject"/>
///   FROM     = <see cref="Name"/>
///   mail     = <see cref="Mail"/>
///   MESSAGE  = <see cref="Message"/>
///   time     = サーバ向けのエポック秒 (PostClient 内で生成)
///   submit   = "書き込む" or "新規スレッド作成" (PostClient 内で生成)
/// </summary>
public sealed record PostRequest(
    Board   Board,
    string? ThreadKey,
    string? Subject,
    string  Name,
    string  Mail,
    string  Message)
{
    public bool IsNewThread => !string.IsNullOrEmpty(Subject) && string.IsNullOrEmpty(ThreadKey);
    public bool IsReply     =>  string.IsNullOrEmpty(Subject) && !string.IsNullOrEmpty(ThreadKey);
}
