using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Models;
using ChBrowser.Services.Bbs;
using ChBrowser.Services.Donguri;
using ChBrowser.Services.Storage;

namespace ChBrowser.Services.Api;

/// <summary>
/// 書き込み (レス + スレ立て) クライアント。プロトコルの差 (エンドポイント / フィールド名 / 文字コード / 結果判定) は
/// 提供者 (<see cref="IBbsProvider.BuildPostSubmission"/> / <see cref="IBbsProvider.ClassifyPostResponse"/>) が持ち、
/// ここは「フォームを符号化して送り、結果を判定し、必要なら 2 段目を送る」だけを行う。
///
/// 設計書 §3.4 の 2 段階 POST:
///   1) フォームを提供者の文字コードで URL エンコードして POST → サーバが確認画面 (Cookie 要求 + hidden トークン) を返す
///   2) 1 段目レスポンス HTML の &lt;form&gt; から &lt;input&gt; (hidden 含む) を全部抜き出して body を作り直し、
///      Set-Cookie で受け取った Cookie を付けて再 POST → 完了 or エラー画面
///
/// 「同 body + Cookie」だけでは現行 5ch.io は通らないことがあるため (yuki/hana/mona 等の
/// hidden トークンをサーバが新発行して提示してくる)、2 段目は HTML から再構成した body を使う。
///
/// 認証 (B11) は HTTP ヘッダではなく Cookie で行う (5ch のみ = <see cref="PostFormSpec.UsesDonguriAuth"/>):
///   - MonaTicket / acorn は <see cref="DonguriService"/> 経由で <see cref="CookieJar"/> に格納
///   - 各リクエストで ApplyToRequest → MergeFromResponse の対で更新
///   どんぐりを使わない提供者 (したらば / まちBBS / エッヂ) では <see cref="DonguriService"/> に一切触れない。
///   <see cref="PostFormSpec.PersistsCookies"/> の提供者 (エッヂ) は代わりに <see cref="ProviderCookieJars"/> の保管を
///   同じ ApplyToRequest → MergeFromResponse の対で更新し、終了時に保存する。
///
/// 結果判定は文言マッチで、サーバ側の文言変更で誤分類が起きうるため <see cref="PostResult.RawHtmlSnippet"/> も併記する。
/// </summary>
public sealed class PostClient
{
    private readonly MonazillaClient _http;
    private readonly DonguriService  _donguri;
    /// <summary>書き込み成功時に kakikomi.txt へ append する (任意 — 注入されていなければ no-op)。</summary>
    private readonly KakikomiLog?    _kakikomi;
    /// <summary>提供者ごとの Cookie 保管 (エッヂ等)。注入されていなければ Cookie を持ち回らない。</summary>
    private readonly ProviderCookieJars? _cookieJars;

    public PostClient(MonazillaClient http, DonguriService donguri, KakikomiLog? kakikomi = null,
                      ProviderCookieJars? cookieJars = null)
    {
        _http       = http;
        _donguri    = donguri;
        _kakikomi   = kakikomi;
        _cookieJars = cookieJars;
    }

    /// <summary>1 件の投稿を実行して結果を返す。どんぐりを使う提供者では CookieJar/state.json を終了時に必ず保存。</summary>
    public async Task<PostResult> PostAsync(PostRequest request, CancellationToken ct = default)
    {
        var provider = BbsRegistry.ResolveOrDefault(request.Board.Host);
        if ((provider.Capabilities & BbsCapabilities.Posting) == 0)
            throw new InvalidOperationException("この掲示板は書き込みに対応していません。");

        // 提供者ごとの Cookie 保管 (エッヂ等)。認証 Cookie を既に持っていれば手入力の認証トークンは送らない
        // (サーバは Cookie を優先するので、メール欄に余計な "#…" を付けないため)。
        var jar = provider.PostForm.PersistsCookies && _cookieJars is not null ? _cookieJars.Get(provider) : null;
        if (jar is not null && !string.IsNullOrWhiteSpace(request.AuthToken) &&
            provider.PostForm.AuthCookieName is { Length: > 0 } authCookie &&
            provider.PostEndpointUrl(request.Board) is { } endpointForCookie &&
            jar.Find(new Uri(endpointForCookie).Host, authCookie) is not null)
        {
            request = request with { AuthToken = null };
        }

        var sub         = provider.BuildPostSubmission(request);
        var uri         = sub.Endpoint;
        var useDonguri  = provider.PostForm.UsesDonguriAuth;
        var authMode    = useDonguri ? request.AuthMode : PostAuthMode.None;
        var origBody    = EncodeForm(sub.Fields, sub.Encoding);

        try
        {
            // ---- 1 段目 ----
            Debug.WriteLine($"[PostClient] STAGE 1 → {uri} (provider={provider.Id}, auth={authMode})");
            var first  = await SendAsync(uri, origBody, sub, authMode, useDonguri, jar, ct).ConfigureAwait(false);
            var html1  = await DecodeHtmlAsync(first, sub.Encoding, ct).ConfigureAwait(false);
            DumpResponseDiagnostics("STAGE 1", first, html1, useDonguri);
            var ctx1   = ContextOf(first, uri);
            var class1 = provider.ClassifyPostResponse(html1, ctx1);
            LogPostResult(provider, "STAGE 1", ctx1, class1, html1);

            if (class1.Outcome == PostOutcome.Success)
            {
                if (useDonguri) _donguri.NoteWriteSucceeded();
                AppendKakikomi(request);
                return class1;
            }
            if (class1.Outcome != PostOutcome.NeedsConfirm)
            {
                // 規制 / Lv不足 / brokenAcorn 等は 1 段目で確定する場合がある
                if (useDonguri && class1.Outcome == PostOutcome.BrokenAcorn) _donguri.HandleBrokenAcorn(authMode);
                return class1;
            }

            // ---- 2 段目 (確認画面 HTML から hidden form を取り出して再 POST) ----
            // 確認画面の <form> 内 <input> をすべて抽出して body にする。サーバが新規発行する
            // hidden トークン (yuki/hana/mona/agree 等) を漏らさないために重要。
            // 1 段目の元入力 (FROM/mail/MESSAGE/...) はサーバ側で hidden に値ごとリレーされてくるので、
            // form 内の <input> がそのまま 2 段目 body として通用する。万一 hidden が空っぽだったら
            // 元 body にフォールバック (動かないより試す価値はある)。
            var hiddenBody = BuildFormBodyFromHtmlForm(html1, sub.Encoding);
            var body2      = hiddenBody ?? origBody;
            Debug.WriteLine($"[PostClient] STAGE 2 → {uri} (body source: {(hiddenBody is null ? "fallback original" : "extracted from form")})");

            var second = await SendAsync(uri, body2, sub, authMode, useDonguri, jar, ct).ConfigureAwait(false);
            var html2  = await DecodeHtmlAsync(second, sub.Encoding, ct).ConfigureAwait(false);
            DumpResponseDiagnostics("STAGE 2", second, html2, useDonguri);
            var ctx2   = ContextOf(second, uri);
            var class2 = provider.ClassifyPostResponse(html2, ctx2);
            LogPostResult(provider, "STAGE 2", ctx2, class2, html2);

            if (class2.Outcome == PostOutcome.Success)
            {
                if (useDonguri) _donguri.NoteWriteSucceeded();
                AppendKakikomi(request);
            }
            if (useDonguri && class2.Outcome == PostOutcome.BrokenAcorn) _donguri.HandleBrokenAcorn(authMode);
            return class2;
        }
        finally
        {
            if (useDonguri)
            {
                try { await _donguri.SaveAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) { Debug.WriteLine($"[PostClient] donguri save failed: {ex.Message}"); }
            }
            if (jar is not null)
            {
                try { await jar.SaveAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) { Debug.WriteLine($"[PostClient] cookie save failed ({provider.Id}): {ex.Message}"); }
            }
        }
    }

    /// <summary>HTTP レスポンスの Set-Cookie / 現在の CookieJar / HTML 抜粋を Debug 出力に流す。
    /// 投稿が通らない時の調査用。リリース版でもアプリ性能には影響しない (Debug.WriteLine は
    /// Release ビルドで no-op)。</summary>
    private void DumpResponseDiagnostics(string label, HttpResponseMessage resp, string html, bool useDonguri)
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
        if (useDonguri)
        {
            var jarMona   = _donguri.MonaTicketValue;
            var jarAcorn  = _donguri.AcornValue;
            var anonAcorn = _donguri.AnonAcornValue;
            Debug.WriteLine($"[PostClient]   jar.MonaTicket={(jarMona  is null ? "(null)" : "(present)")}, jar.acorn={(jarAcorn is null ? "(null)" : "(present)")}, anon.acorn={(anonAcorn is null ? "(null)" : "(present)")}");
        }
        var snippet = html.Length > 1200 ? html[..1200] + " …(truncated)" : html;
        Debug.WriteLine($"[PostClient]   HTML: {snippet.Replace('\n', ' ').Replace('\r', ' ')}");
    }

    /// <summary>確認画面 HTML から &lt;form&gt; 配下の &lt;input&gt; を全部拾って 2 段目 body にする。
    /// hidden 含む全 input を採用 (= サーバが付けてくる yuki/hana/mona 等の確認トークンを取りこぼさない)。
    /// submit ボタン行は name 属性付きのものだけ採用 (押されたボタン相当を再送するため)。
    /// HTML に form / input が見当たらない場合は <c>null</c>。
    ///
    /// 対象 form は action に <c>.cgi</c> を含むもの (5ch bbs.cgi / したらば・まちBBS write.cgi)、無ければ最初の form。
    /// 値はすでにテキストとしてパースされているので、書き出し時に提供者の文字コードのバイトへ再エンコードする。
    /// HTML 上の name="" / value="" はサーバが HTML エンティティ化している前提で <c>WebUtility.HtmlDecode</c>
    /// する。</summary>
    private static byte[]? BuildFormBodyFromHtmlForm(string html, Encoding encoding)
    {
        var formMatch = Regex.Match(html,
            @"<form\b[^>]*?action\s*=\s*[""'][^""']*\.cgi[^""']*[""'][\s\S]*?</form>",
            RegexOptions.IgnoreCase);
        var formHtml = formMatch.Success
            ? formMatch.Value
            // .cgi action が見つからない場合でも、最初の <form>...</form> を試す
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
        return EncodeForm(fields, encoding);
    }

    /// <summary>フィールド列を <c>application/x-www-form-urlencoded</c> のバイト列にする
    /// (各名前 / 値を <see cref="FormEncode"/> で % エスケープし <c>&amp;</c> で連結)。</summary>
    private static byte[] EncodeForm(IReadOnlyList<KeyValuePair<string, string>> fields, Encoding encoding)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0) sb.Append('&');
            sb.Append(FormEncode(fields[i].Key, encoding)).Append('=').Append(FormEncode(fields[i].Value, encoding));
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

    /// <summary>提供者の文字コードのバイトに変換した上で URL エンコードする (5ch / まちBBS は SJIS、したらばは EUC-JP の % エスケープを期待する)。
    ///
    /// <para>その文字コードで表現できない文字 (絵文字等) は <see cref="HtmlEntityFallbackEncoder"/> で
    /// <c>&amp;#xNNNN;</c> の数値文字参照に倒す。素の <c>Encoding.GetEncoding(932)</c> は既定の
    /// 置換フォールバックで '?' に潰してしまい (サロゲートペアなら "??")、投稿内容が壊れるため。
    /// 数値文字参照は 5ch/Monazilla の慣習で、dat 側は <see cref="DatParser"/> の HtmlDecode により
    /// 元の文字へ復元される (= 往復して同じ表示になる)。サロゲートペアは 1 コードポイント =
    /// 1 参照にまとめられる。</para></summary>
    private static string FormEncode(string s, Encoding encoding)
    {
        var bytes = HtmlEntityFallbackEncoder.GetBytes(s, encoding);
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

    private async Task<HttpResponseMessage> SendAsync(Uri uri, byte[] body, PostSubmission sub, PostAuthMode authMode,
                                                      bool useDonguri, CookieJar? jar, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new ByteArrayContent(body),
        };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-www-form-urlencoded")
        {
            CharSet = sub.CharsetName,
        };
        req.Headers.TryAddWithoutValidation("Referer", sub.Referer);
        req.Headers.TryAddWithoutValidation("Origin",  $"{uri.Scheme}://{uri.Host}");
        if (useDonguri) _donguri.ApplyToRequest(req, authMode);
        jar?.ApplyToRequest(req);

        var resp = await _http.Http.SendAsync(req, ct).ConfigureAwait(false);
        // 提供者ごとの保管: サーバが発行 / 更新した Cookie (エッヂの edge-token / tinker-token) を取り込む
        jar?.MergeFromResponse(resp);
        // モードを伝えて Set-Cookie の振り分けを行う:
        //   MailAuth → jar (= 通常 acorn / メール認証 acorn = 同じスロット = jar 側) を更新
        //   Cookie / None → state.json の anon acorn / anon MonaTicket を更新 (= jar は触らない)
        if (useDonguri) _donguri.MergeFromResponse(resp, authMode);
        return resp;
    }

    /// <summary>応答から <see cref="PostResponseContext"/> を組む。HttpClient はリダイレクトを自動追従するため、
    /// 最終 URI がエンドポイントと違えば「リダイレクトされた」とみなす (3xx が素で返った場合も同様)。</summary>
    private static PostResponseContext ContextOf(HttpResponseMessage resp, Uri endpoint)
    {
        var status   = (int)resp.StatusCode;
        var finalUri = resp.RequestMessage?.RequestUri;
        var redirected = (status >= 300 && status < 400)
                      || (finalUri is not null && !string.Equals(finalUri.AbsoluteUri, endpoint.AbsoluteUri, StringComparison.Ordinal));
        return new PostResponseContext(status, redirected, finalUri, endpoint);
    }

    /// <summary>投稿の判定根拠をアプリのログに残す (提供者 / ステータス / 最終 URI / 判定 / 本文冒頭)。
    /// 5ch 以外の掲示板は応答文言の実例が少なく誤判定が起き得るため、後から突き合わせられるようにしておく。</summary>
    private static void LogPostResult(IBbsProvider provider, string stage, PostResponseContext ctx, PostResult result, string html)
    {
        var head = PostResponseHtml.ExtractBodyText(html);
        if (head.Length > 200) head = head[..200] + "…";
        ChBrowser.Services.Logging.LogService.Instance.Write(
            $"[post] {provider.Id} {stage}: status={ctx.StatusCode} redirected={ctx.Redirected} final={ctx.FinalUri?.AbsoluteUri ?? "-"} → {result.Outcome} | {head}");
    }

    private static async Task<string> DecodeHtmlAsync(HttpResponseMessage resp, Encoding encoding, CancellationToken ct)
    {
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return encoding.GetString(bytes);
    }

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
