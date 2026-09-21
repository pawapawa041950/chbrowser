using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ChBrowser.Models;

namespace ChBrowser.Services.Bbs;

/// <summary>投稿フォームで使える項目。投稿ダイアログはこれを見て UI を出し分ける
/// (<c>doc/multi-bbs-design.md</c> §7「投稿フォーム定義」)。</summary>
/// <param name="SupportsName">名前欄を持つ。</param>
/// <param name="SupportsMail">メール欄 (sage 含む) を持つ。</param>
/// <param name="SupportsNewThread">スレ立て (題名欄) ができる。</param>
/// <param name="UsesDonguriAuth">5ch のどんぐり Cookie (acorn / MonaTicket / メール認証) を投稿に使う。
/// false の提供者では認証モードは常に <see cref="PostAuthMode.None"/> で、<see cref="Donguri.DonguriService"/> には一切触れない。</param>
/// <param name="PersistsCookies">投稿応答の Set-Cookie を提供者ごとの保管 (<see cref="ProviderCookieJars"/>、<c>data/&lt;root&gt;/cookies.txt</c>) に
/// 持ち、次の投稿から送る (エッヂの edge-token / tinker-token)。どんぐりとは独立。</param>
/// <param name="AuthCookieName">「認証済み」を表す Cookie 名 (エッヂ: <c>edge-token</c>)。保管にこれがあれば
/// 手入力の認証トークン (<see cref="PostRequest.AuthToken"/>) は送らない。null なら判定しない。</param>
public sealed record PostFormSpec(
    bool SupportsName,
    bool SupportsMail,
    bool SupportsNewThread,
    bool UsesDonguriAuth,
    bool PersistsCookies = false,
    string? AuthCookieName = null);

/// <summary>1 段目 POST の内容。提供者が <see cref="IBbsProvider.BuildPostSubmission"/> で組み立て、
/// <see cref="Api.PostClient"/> はこれを <c>application/x-www-form-urlencoded</c> に符号化して送るだけ。</summary>
/// <param name="Endpoint">POST 先。</param>
/// <param name="Fields">送信順序どおりのフィールド列 (名前 / 値は生の Unicode 文字列)。</param>
/// <param name="Encoding">フィールドの文字コード (5ch / まちBBS: Shift_JIS、したらば: EUC-JP)。表現できない文字は数値文字参照に倒す。</param>
/// <param name="CharsetName">Content-Type の charset に書く名前 (例 "Shift_JIS", "EUC-JP")。</param>
/// <param name="Referer">Referer ヘッダ (板 URL またはスレページ URL)。</param>
public sealed record PostSubmission(
    Uri Endpoint,
    IReadOnlyList<KeyValuePair<string, string>> Fields,
    Encoding Encoding,
    string CharsetName,
    string Referer);

/// <summary>投稿レスポンスの HTML 以外の手がかり。2ch 互換 CGI (したらば / まちBBS) は成功時に
/// スレページへ 302 で飛ばすことがあり、HttpClient が追従すると本文はスレの HTML になる
/// (= 文言判定では「規制」等のレス本文を拾って誤判定する)。リダイレクトの有無を判定に使う。</summary>
/// <param name="StatusCode">最終応答のステータス。</param>
/// <param name="Redirected">3xx 応答、または自動追従の結果 <see cref="FinalUri"/> がエンドポイントと異なる。</param>
/// <param name="FinalUri">自動追従後の URI (追従なしなら null)。</param>
/// <param name="Endpoint">POST したエンドポイント。</param>
public sealed record PostResponseContext(int StatusCode, bool Redirected, Uri? FinalUri, Uri Endpoint)
{
    public static PostResponseContext None(Uri endpoint) => new(200, false, null, endpoint);
}

/// <summary>投稿レスポンス HTML の判定で提供者が共有する小道具 (本文テキスト抽出 / 文言マッチ / 要約)。
/// 旧 <c>PostClient</c> の private ヘルパをそのまま移したもの。</summary>
internal static class PostResponseHtml
{
    private static readonly Regex BodyTextRegex = new(@"<body[^>]*>(?<body>[\s\S]*?)</body>", RegexOptions.IgnoreCase);
    private static readonly Regex TagRegex      = new(@"<[^>]+>", RegexOptions.None);
    private static readonly Regex WsRegex       = new(@"\s+",     RegexOptions.None);

    /// <summary>確認画面検出の手がかり: 2 段目を要求する画面に含まれる文言 / hidden トークン名。</summary>
    public static readonly string[] ConfirmTokens = { "書き込み確認", "通常書き込み", "name=\"yuki\"", "name=\"hana\"", "name=\"mona\"" };

    /// <summary>成功画面の文言 (旧来 "書きこみました" を含むのが通例)。</summary>
    public static readonly string[] SuccessTokens =
    {
        "書きこみました", "書き込みました", "書き込み完了",
        // 2ch 互換 CGI (したらば / まちBBS) の古典的な成功画面
        "書きこみが終わりました", "書きこみが終りました", "書き込みが終わりました", "投稿しました", "投稿が完了",
    };

    /// <summary>エラー文言を探す範囲 (本文テキストの先頭からの文字数)。エラー画面は短く冒頭に理由を書くのに対し、
    /// リダイレクト追従で届いたスレ HTML は長く、レス本文中の「規制」等を拾ってしまうため範囲を絞る。</summary>
    private const int ErrorZoneChars = 400;

    public static string ExtractBodyText(string html)
    {
        var m   = BodyTextRegex.Match(html);
        var src = m.Success ? m.Groups["body"].Value : html;
        var stripped  = TagRegex.Replace(src, " ");
        var collapsed = WsRegex.Replace(stripped, " ").Trim();
        return WebUtility.HtmlDecode(collapsed);
    }

    public static bool Contains(string s, string[] tokens)
    {
        foreach (var t in tokens)
            if (s.Contains(t, StringComparison.Ordinal)) return true;
        return false;
    }

    public static string ShortenForUser(string s)
        => s.Length <= 200 ? s : s[..200] + "…";

    public static string Snippet(string bodyText)
        => bodyText.Length > 400 ? bodyText[..400] : bodyText;

    /// <summary>したらば / まちBBS 向けの汎用判定 (どんぐり系の判定を持たない)。
    /// 成功文言 → Success、規制 / エラー文言 → BlockedByRule、確認画面 → NeedsConfirm、それ以外 → UnknownError。</summary>
    public static PostResult ClassifyGeneric(string html, PostResponseContext? context = null)
    {
        var bodyText = ExtractBodyText(html);
        var snippet  = Snippet(bodyText);

        // 成功時にスレページへリダイレクトする CGI: 追従先の HTML を文言判定に掛けると誤判定するので、
        // リダイレクトされた時点で成功とみなす (エラーはリダイレクトせずその場で HTML を返す)。
        if (context is { Redirected: true })
            return new PostResult(PostOutcome.Success, "", snippet);

        if (Contains(html, SuccessTokens))
            return new PostResult(PostOutcome.Success, "", snippet);

        // エラー判定は本文冒頭と <title> に限定する (長いページの途中に出る「規制」等は拾わない)。
        var errorZone = bodyText.Length > ErrorZoneChars ? bodyText[..ErrorZoneChars] : bodyText;
        var title     = TitleOf(html);
        if (LooksLikeError(errorZone) || LooksLikeError(title))
            return new PostResult(PostOutcome.BlockedByRule, ShortenForUser(bodyText), snippet);

        if (Contains(html, ConfirmTokens))
            return new PostResult(PostOutcome.NeedsConfirm, "", snippet);

        return new PostResult(PostOutcome.UnknownError, ShortenForUser(bodyText), snippet);
    }

    private static bool LooksLikeError(string s)
        => s.Contains("規制") || s.Contains("ERROR") || s.Contains("ＥＲＲＯＲ") || s.Contains("エラー") || s.Contains("書き込めません");

    private static readonly Regex TitleRegex = new(@"<title[^>]*>(?<t>[\s\S]*?)</title>", RegexOptions.IgnoreCase);
    private static string TitleOf(string html)
    {
        var m = TitleRegex.Match(html);
        return m.Success ? WebUtility.HtmlDecode(m.Groups["t"].Value) : "";
    }

    /// <summary>投稿フォームの <c>time</c> / <c>TIME</c> に入れるエポック秒。</summary>
    public static string UnixNow()
        => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
}
