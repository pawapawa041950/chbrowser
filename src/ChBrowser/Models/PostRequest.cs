namespace ChBrowser.Models;

/// <summary>書き込み時にどの Cookie 集合を投稿リクエストに添付するか。
/// 5ch のどんぐり階層に対応する 3 段:
/// <list type="bullet">
/// <item><description><see cref="None"/>: 一切の Cookie を送らない (= 初回投稿相当 / 規制テスト等)</description></item>
/// <item><description><see cref="Cookie"/>: acorn (クッキーどんぐり) と MonaTicket だけ送る (= メール認証 Cookie は外す)。
///   メール認証で Lv が上がりすぎて目立つのを避けたい時に使う。</description></item>
/// <item><description><see cref="MailAuth"/>: 持っている Cookie を全部送る (= メール認証込み、現状の既定動作)。</description></item>
/// </list>
/// </summary>
public enum PostAuthMode
{
    None,
    Cookie,
    MailAuth,
}

/// <summary>
/// 投稿リクエスト 1 件分 (提供者非依存)。実際のフィールド名 / エンドポイント / 文字コードは
/// 提供者の <see cref="Services.Bbs.IBbsProvider.BuildPostSubmission"/> が決める。
/// レス書き込みは <see cref="ThreadKey"/> を、スレ立ては <see cref="Subject"/> を持つ
/// (両方持つことは無い想定だが、検証は <see cref="Services.Api.PostClient"/> 側で行う)。
///
/// 5ch (bbs.cgi、設計書 §3.4) の場合の対応:
///   bbs      = <see cref="Board"/>.DirectoryName (board.directoryName)
///   key      = <see cref="ThreadKey"/>
///   subject  = <see cref="Subject"/>
///   FROM     = <see cref="Name"/>
///   mail     = <see cref="Mail"/>
///   MESSAGE  = <see cref="Message"/>
///   time     = サーバ向けのエポック秒 (提供者側で生成)
///   submit   = "書き込む" or "新規スレッド作成" (提供者側で生成)
///
/// <see cref="AuthMode"/> はサーバには送らず、PostClient が Cookie ヘッダ組み立て時に使う
/// (どんぐりを使わない提供者では無視される)。
/// </summary>
public sealed record PostRequest(
    Board   Board,
    string? ThreadKey,
    string? Subject,
    string  Name,
    string  Mail,
    string  Message,
    PostAuthMode AuthMode = PostAuthMode.MailAuth,
    /// <summary>レス書き込み時の元スレタイトル (kakikomi.txt 等の記録用)。新スレ立て時は null で OK。</summary>
    string? ThreadTitle = null,
    /// <summary>掲示板側の認証トークン (設定 <see cref="AppConfig.PostAuthTokens"/>、任意)。エッヂではメール欄に <c>#トークン</c> として付ける。
    /// 提供者の認証 Cookie (<see cref="Services.Bbs.PostFormSpec.AuthCookieName"/>) を既に持っていれば PostClient が外す。null / 空なら未使用。</summary>
    string? AuthToken = null,
    /// <summary>返信先の投稿 ID (reddit: <c>t1_xxx</c>。null ならスレ本体への返信)。
    /// <see cref="Services.Bbs.PostFormSpec.SupportsReplyTarget"/> の提供者だけが使う。</summary>
    string? ReplyTargetExternalId = null)
{
    public bool IsNewThread => !string.IsNullOrEmpty(Subject) && string.IsNullOrEmpty(ThreadKey);
    public bool IsReply     =>  string.IsNullOrEmpty(Subject) && !string.IsNullOrEmpty(ThreadKey);

    /// <summary>kakikomi.txt 等で使う「実効スレタイトル」 — 新スレなら <see cref="Subject"/>、レスなら <see cref="ThreadTitle"/>。</summary>
    public string EffectiveSubject => IsNewThread ? (Subject ?? "") : (ThreadTitle ?? "");

    /// <summary>kakikomi.txt 等で使う表示 URL — レスなら提供者の正規スレ URL、新スレ立てなら板トップ。
    /// (新スレは投稿成功時点で thread key が未確定のため板 URL を採用)</summary>
    public string PageUrl => IsReply
        ? Services.Bbs.BbsRegistry.ResolveOrDefault(Board.Host).ThreadUrl(Board.Host, Board.DirectoryName, ThreadKey!)
        : Board.Url;
}
