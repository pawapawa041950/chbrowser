using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Models;
using ChBrowser.Services.Donguri;
using ChBrowser.Services.Storage;
// ChBrowser.Models.PostAuthMode を SendAsync シグネチャで使うが、namespace 既に上で using 済

namespace ChBrowser.Services.Api;

/// <summary>
/// bbs.cgi への書き込み (レス + スレ立て) クライアント。
///
/// 設計書 §3.4 に従って 2 段階 POST を実装する:
///   1) フォームを SJIS URL エンコードして POST → サーバが確認画面 (Cookie 要求 + hidden トークン) を返す
///   2) 1 段目レスポンス HTML の &lt;form&gt; から &lt;input&gt; (hidden 含む) を全部抜き出して body を作り直し、
///      Set-Cookie で受け取った Cookie を付けて再 POST → 完了 or エラー画面
///
/// 「同 body + Cookie」だけでは現行 5ch.io は通らないことがあるため (yuki/hana/mona 等の
/// hidden トークンをサーバが新発行して提示してくる)、2 段目は HTML から再構成した body を使う。
///
/// 認証 (B11) は HTTP ヘッダではなく Cookie で行う:
///   - MonaTicket / acorn は <see cref="DonguriService"/> 経由で <see cref="CookieJar"/> に格納
///   - 各リクエストで ApplyToRequest → MergeFromResponse の対で更新
///
/// レスポンス HTML から成否/エラー種別を判定する。判定ルールは 5ch サーバの定文言ベースで、
/// 5ch 側の文言変更で誤分類が起きうるため <see cref="PostResult.RawHtmlSnippet"/> も併記する。
/// </summary>
public sealed class PostClient
{
    /// <summary>確認画面検出の手がかり: bbs.cgi が 2 段目を要求するときに必ず含まれる文言。</summary>
    private static readonly string[] ConfirmTokens = { "書き込み確認", "通常書き込み", "name=\"yuki\"", "name=\"hana\"", "name=\"mona\"" };

    /// <summary>成功画面の文言 (旧来 "書きこみました" を含むのが通例)。</summary>
    private static readonly string[] SuccessTokens = { "書きこみました", "書き込みました", "書き込み完了" };

    private static readonly Regex BodyTextRegex = new(@"<body[^>]*>(?<body>[\s\S]*?)</body>", RegexOptions.IgnoreCase);
    private static readonly Regex TagRegex      = new(@"<[^>]+>", RegexOptions.None);
    private static readonly Regex WsRegex       = new(@"\s+",     RegexOptions.None);

    private readonly MonazillaClient _http;
    private readonly DonguriService  _donguri;
    /// <summary>書き込み成功時に kakikomi.txt へ append する (任意 — 注入されていなければ no-op)。</summary>
    private readonly KakikomiLog?    _kakikomi;

    public PostClient(MonazillaClient http, DonguriService donguri, KakikomiLog? kakikomi = null)
    {
        _http     = http;
        _donguri  = donguri;
        _kakikomi = kakikomi;
    }

    /// <summary>1 件の投稿を実行して結果を返す。CookieJar/state.json は終了時に必ず保存。</summary>
    public async Task<PostResult> PostAsync(PostRequest request, CancellationToken ct = default)
    {
        // Board.Url は "https://hayabusa9.5ch.io/news/" なので bbs.cgi はホストルート直下の test/bbs.cgi
        var uri      = new Uri(new Uri(request.Board.Url), "/test/bbs.cgi");
        var origBody = BuildSjisFormBodyFromRequest(request);

        try
        {
            // ---- 1 段目 ----
            Debug.WriteLine($"[PostClient] STAGE 1 → {uri} (auth={request.AuthMode})");
            var first  = await SendAsync(uri, origBody, request.Board.Url, request.AuthMode, ct).ConfigureAwait(false);
            var html1  = await DecodeSjisHtmlAsync(first, ct).ConfigureAwait(false);
            DumpResponseDiagnostics("STAGE 1", first, html1);
            var class1 = Classify(html1);

            if (class1.Outcome == PostOutcome.Success)
            {
                _donguri.NoteWriteSucceeded();
                AppendKakikomi(request);
                return class1;
            }
            if (class1.Outcome != PostOutcome.NeedsConfirm)
            {
                // 規制 / Lv不足 / brokenAcorn 等は 1 段目で確定する場合がある
                if (class1.Outcome == PostOutcome.BrokenAcorn) _donguri.HandleBrokenAcorn();
                return class1;
            }

            // ---- 2 段目 (確認画面 HTML から hidden form を取り出して再 POST) ----
            // 確認画面の <form> 内 <input> をすべて抽出して body にする。サーバが新規発行する
            // hidden トークン (yuki/hana/mona/agree 等) を漏らさないために重要。
            // 1 段目の元入力 (FROM/mail/MESSAGE/...) はサーバ側で hidden に値ごとリレーされてくるので、
            // form 内の <input> がそのまま 2 段目 body として通用する。万一 hidden が空っぽだったら
            // 元 body にフォールバック (動かないより試す価値はある)。
            var hiddenBody = BuildSjisFormBodyFromHtmlForm(html1, request);
            var body2      = hiddenBody ?? origBody;
            Debug.WriteLine($"[PostClient] STAGE 2 → {uri} (body source: {(hiddenBody is null ? "fallback original" : "extracted from form")})");

            var second = await SendAsync(uri, body2, request.Board.Url, request.AuthMode, ct).ConfigureAwait(false);
            var html2  = await DecodeSjisHtmlAsync(second, ct).ConfigureAwait(false);
            DumpResponseDiagnostics("STAGE 2", second, html2);
            var class2 = Classify(html2);

            if (class2.Outcome == PostOutcome.Success)        { _donguri.NoteWriteSucceeded(); AppendKakikomi(request); }
            if (class2.Outcome == PostOutcome.BrokenAcorn)    _donguri.HandleBrokenAcorn();
            return class2;
        }
        finally
        {
            try { await _donguri.SaveAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { Debug.WriteLine($"[PostClient] donguri save failed: {ex.Message}"); }
        }
    }

    /// <summary>HTTP レスポンスの Set-Cookie / 現在の CookieJar / HTML 抜粋を Debug 出力に流す。
    /// 投稿が通らない時の調査用。リリース版でもアプリ性能には影響しない (Debug.WriteLine は
    /// Release ビルドで no-op)。</summary>
    private void DumpResponseDiagnostics(string label, HttpResponseMessage resp, string html)
    {
        Debug.WriteLine($"[PostClient] {label} status={(int)resp.StatusCode} {resp.StatusCode}");
        if (resp.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var sc in setCookies) Debug.WriteLine($"[PostClient]   Set-Cookie: {sc}");
        }
        else
        {
            Debug.WriteLine("[PostClient]   Set-Cookie: (none)");
        }
        var jarMona  = _donguri.MonaTicketValue;
        var jarAcorn = _donguri.AcornValue;
        Debug.WriteLine($"[PostClient]   jar.MonaTicket={(jarMona  is null ? "(null)" : "(present)")}, jar.acorn={(jarAcorn is null ? "(null)" : "(present)")}");
        var snippet = html.Length > 1200 ? html[..1200] + " …(truncated)" : html;
        Debug.WriteLine($"[PostClient]   HTML: {snippet.Replace('\n', ' ').Replace('\r', ' ')}");
    }

    /// <summary>1 段目 POST の body をユーザ入力 (PostRequest) から SJIS 形式で組み立てる。</summary>
    private static byte[] BuildSjisFormBodyFromRequest(PostRequest req)
    {
        var unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var fields = new List<KeyValuePair<string, string>>
        {
            new("FROM",    req.Name),
            new("mail",    req.Mail),
            new("MESSAGE", req.Message),
            new("bbs",     req.Board.DirectoryName),
            new("time",    unix),
        };
        if (req.IsReply)     fields.Add(new("key",     req.ThreadKey!));
        if (req.IsNewThread) fields.Add(new("subject", req.Subject!));
        fields.Add(new("submit", req.IsNewThread ? "新規スレッド作成" : "書き込む"));
        return EncodeAsSjisForm(fields);
    }

    /// <summary>確認画面 HTML から &lt;form&gt; 配下の &lt;input&gt; を全部拾って 2 段目 body にする。
    /// hidden 含む全 input を採用 (= サーバが付けてくる yuki/hana/mona 等の確認トークンを取りこぼさない)。
    /// submit ボタン行は name 属性付きのものだけ採用 (押されたボタン相当を再送するため)。
    /// HTML に form / input が見当たらない場合は <c>null</c>。
    ///
    /// 値はすでに SJIS テキストとしてパースされているので、書き出し時に SJIS バイトへ再エンコードする。
    /// HTML 上の name="" / value="" はサーバが HTML エンティティ化している前提で <c>WebUtility.HtmlDecode</c>
    /// する。</summary>
    private static byte[]? BuildSjisFormBodyFromHtmlForm(string html, PostRequest req)
    {
        var formMatch = Regex.Match(html,
            @"<form\b[^>]*?action\s*=\s*[""'][^""']*bbs\.cgi[^""']*[""'][\s\S]*?</form>",
            RegexOptions.IgnoreCase);
        var formHtml = formMatch.Success
            ? formMatch.Value
            // bbs.cgi action が見つからない場合でも、最初の <form>...</form> を試す
            : Regex.Match(html, @"<form\b[\s\S]*?</form>", RegexOptions.IgnoreCase).Value;
        if (string.IsNullOrEmpty(formHtml)) return null;

        var fields = new List<KeyValuePair<string, string>>();
        foreach (Match m in Regex.Matches(formHtml, @"<input\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var tag    = m.Value;
            var type   = AttrOf(tag, "type") ?? "text";
            var name   = AttrOf(tag, "name");
            var value  = AttrOf(tag, "value") ?? "";
            if (string.IsNullOrEmpty(name)) continue;
            // チェックされていない checkbox / radio はサーバに送らないのが HTML form の挙動なので除外
            if (type.Equals("checkbox", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("radio",    StringComparison.OrdinalIgnoreCase))
            {
                if (!Regex.IsMatch(tag, @"\bchecked\b", RegexOptions.IgnoreCase)) continue;
            }
            // submit 以外、または submit でも name= があれば採用 (5ch は通常 name="submit" value="書き込む")
            fields.Add(new(WebUtility.HtmlDecode(name), WebUtility.HtmlDecode(value)));
        }
        if (fields.Count == 0) return null;
        return EncodeAsSjisForm(fields);
    }

    private static byte[] EncodeAsSjisForm(IReadOnlyList<KeyValuePair<string, string>> fields)
    {
        var sjis = Encoding.GetEncoding(932);
        var sb   = new StringBuilder();
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0) sb.Append('&');
            sb.Append(SjisEncode(fields[i].Key, sjis)).Append('=').Append(SjisEncode(fields[i].Value, sjis));
        }
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>&lt;tag attr="v"&gt; から指定 attr の値を取り出す (ダブル/シングル両対応、属性名は大文字小文字無視)。</summary>
    private static string? AttrOf(string tag, string attr)
    {
        var m = Regex.Match(tag,
            $@"\b{Regex.Escape(attr)}\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))",
            RegexOptions.IgnoreCase);
        return m.Success ? m.Groups["v"].Value : null;
    }

    /// <summary>SJIS バイトに変換した上で URL エンコードする (5ch は SJIS の % エスケープを期待する)。</summary>
    private static string SjisEncode(string s, Encoding sjis)
    {
        var bytes = sjis.GetBytes(s);
        var sb    = new StringBuilder(bytes.Length * 3);
        foreach (var b in bytes)
        {
            // RFC 3986 unreserved + 一部 application/x-www-form-urlencoded 互換 (空白は + ではなく %20 にする)
            var unreserved = (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') || (b >= '0' && b <= '9')
                          || b == '-' || b == '.' || b == '_' || b == '~';
            if (unreserved) sb.Append((char)b);
            else            sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, byte[] body, string referer, PostAuthMode authMode, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new ByteArrayContent(body),
        };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-www-form-urlencoded")
        {
            CharSet = "Shift_JIS",
        };
        req.Headers.TryAddWithoutValidation("Referer", referer);
        req.Headers.TryAddWithoutValidation("Origin",  $"{uri.Scheme}://{uri.Host}");
        _donguri.ApplyToRequest(req, authMode);

        var resp = await _http.Http.SendAsync(req, ct).ConfigureAwait(false);
        _donguri.MergeFromResponse(resp);
        return resp;
    }

    private static async Task<string> DecodeSjisHtmlAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return Encoding.GetEncoding(932).GetString(bytes);
    }

    /// <summary>HTML 全文から PostOutcome を推定する。判定は文言マッチで、誤判定の可能性ありなので
    /// 抜粋を <see cref="PostResult.RawHtmlSnippet"/> に同梱して呼び出し側で目視できるようにしておく。</summary>
    private static PostResult Classify(string html)
    {
        var bodyText = ExtractBodyText(html);
        var snippet  = bodyText.Length > 400 ? bodyText[..400] : bodyText;

        if (Contains(html, SuccessTokens))
            return new PostResult(PostOutcome.Success, "", snippet);

        // broken_acorn は HTML の id/class 名で出ることが多い
        if (html.Contains("broken_acorn", StringComparison.OrdinalIgnoreCase) ||
            bodyText.Contains("どんぐりが壊れています"))
            return new PostResult(PostOutcome.BrokenAcorn, "どんぐりが壊れています。再取得します。", snippet);

        if (bodyText.Contains("レベル") && (bodyText.Contains("不足") || bodyText.Contains("足りません")))
            return new PostResult(PostOutcome.LevelInsufficient, "どんぐりレベルが不足しています。", snippet);

        if (bodyText.Contains("規制") || bodyText.Contains("ERROR") || bodyText.Contains("Forbidden") ||
            bodyText.Contains("書き込めません"))
            return new PostResult(PostOutcome.BlockedByRule, ShortenForUser(bodyText), snippet);

        if (Contains(html, ConfirmTokens))
            return new PostResult(PostOutcome.NeedsConfirm, "", snippet);

        // 何も該当しなければ未分類エラー扱い (後続で Outcome 増やす余地あり)
        return new PostResult(PostOutcome.UnknownError, ShortenForUser(bodyText), snippet);
    }

    private static string ExtractBodyText(string html)
    {
        var m   = BodyTextRegex.Match(html);
        var src = m.Success ? m.Groups["body"].Value : html;
        var stripped = TagRegex.Replace(src, " ");
        var collapsed = WsRegex.Replace(stripped, " ").Trim();
        return WebUtility.HtmlDecode(collapsed);
    }

    private static bool Contains(string s, string[] tokens)
    {
        foreach (var t in tokens)
            if (s.Contains(t, StringComparison.Ordinal)) return true;
        return false;
    }

    private static string ShortenForUser(string s)
        => s.Length <= 200 ? s : s[..200] + "…";

    /// <summary>kakikomi.txt にエントリを追記 (KakikomiLog 注入時のみ)。
    /// PostRequest から JaneXeno 形式に必要な値を集約する。失敗は KakikomiLog 内で吸収。</summary>
    private void AppendKakikomi(PostRequest req)
    {
        if (_kakikomi is null) return;
        _kakikomi.AppendEntry(new KakikomiEntry(
            When:    DateTime.Now,
            Subject: req.EffectiveSubject,
            Url:     req.PageUrl,
            Name:    req.Name,
            Mail:    req.Mail,
            Body:    req.Message));
    }
}
