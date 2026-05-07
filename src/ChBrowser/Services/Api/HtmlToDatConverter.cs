using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ChBrowser.Services.Api;

/// <summary>
/// 5ch.io の <c>read.cgi</c> (dat 落ちアーカイブスレ用) HTML を、本アプリの <see cref="DatParser"/> が
/// パース可能な 5ch dat 形式 (= <c>名前&lt;&gt;メール&lt;&gt;日付 ID:xxx&lt;&gt;本文&lt;&gt;タイトル\n</c>、Shift_JIS) に変換する。
///
/// 背景:
///   5ch.io は dat 落ちスレに対して <c>/&lt;board&gt;/dat/&lt;key&gt;.dat</c> を 404 で返し、
///   <c>?raw=on</c> や <c>?dat=1</c> のような raw 取得経路も提供していない。アーカイブスレの内容は
///   <c>read.cgi</c> 由来の HTML だけで提供されているため、それを HTML から逆変換する必要がある。
///   onihusube/2chAPIProxy の HTMLtoDat.cs を参考に、5ch.io の現行 HTML 構造に合わせて新規実装した。
///
/// 5ch.io 現行 HTML 構造 (= 1 レス分):
/// <code>
/// &lt;div id="N" data-date="..." data-userid="ID:xxx" data-id="N" class="clear post"&gt;
///   &lt;div class="post-header"&gt;
///     &lt;div&gt;
///       &lt;span class="postid"&gt;N&lt;/span&gt;
///       &lt;span class="postusername"&gt;&lt;b&gt;名前&lt;/b&gt;(ﾜｯﾁｮｲ xxxx-yyyy)&lt;b&gt;&lt;/b&gt;&lt;/span&gt;
///       (... 大砲ボタン等の noise)
///     &lt;/div&gt;
///     &lt;span&gt;&lt;span class="date"&gt;2026/...&lt;/span&gt;&lt;span class="uid"&gt;ID:xxx&lt;/span&gt;&lt;/span&gt;
///   &lt;/div&gt;
///   &lt;div class="post-content"&gt;本文 &lt;br&gt; ... &lt;/div&gt;
/// &lt;/div&gt;
/// </code>
/// </summary>
public static class HtmlToDatConverter
{
    /// <summary>各レスブロックの開始タグ。<c>id="数字" ... class="...post..."</c> のパターン。</summary>
    private static readonly Regex PostOpenRe = new(
        @"<div\s+id=""(?<num>\d+)""[^>]*\sclass=""[^""]*\bpost\b[^""]*""[^>]*>",
        RegexOptions.Compiled);

    /// <summary>postusername / post-content 等を入れ子対応で抽出するときの汎用 open/close マッチャ。
    /// <c>&lt;span ...&gt;</c> / <c>&lt;/span&gt;</c> または <c>&lt;div ...&gt;</c> / <c>&lt;/div&gt;</c> を toggle。</summary>
    private static readonly Regex SpanTagRe = new(@"</?span\b[^>]*>", RegexOptions.Compiled);
    private static readonly Regex DivTagRe  = new(@"</?div\b[^>]*>",  RegexOptions.Compiled);

    /// <summary>postusername の開始タグ。balanced-span でこの後の内容を切り出す。</summary>
    private static readonly Regex PostUsernameOpenRe = new(
        @"<span\s+class=""postusername""[^>]*>",
        RegexOptions.Compiled);

    /// <summary>post-content の開始タグ。</summary>
    private static readonly Regex PostContentOpenRe = new(
        @"<div\s+class=""post-content""[^>]*>",
        RegexOptions.Compiled);

    /// <summary>postusername 内の mailto: (= 名前部に sage 等のメール指定があった場合)。
    /// CloudFlare Email Protection (<c>/cdn-cgi/l/email-protection</c>) は obfuscate されているので拾えない (= 空メール扱い)。</summary>
    private static readonly Regex MailtoRe = new(
        @"<a\s+[^>]*href=""mailto:(?<mail>[^""]*)""",
        RegexOptions.Compiled);

    /// <summary>&lt;span class="date"&gt;日付&lt;/span&gt;。日付テキストにはタグが入らない想定 (= [^&lt;]* で十分)。</summary>
    private static readonly Regex DateSpanRe = new(
        @"<span\s+class=""date"">(?<date>[^<]*)</span>",
        RegexOptions.Compiled);

    /// <summary>&lt;span class="uid"&gt;ID:xxx&lt;/span&gt;。dat の date フィールドに連結する。</summary>
    private static readonly Regex UidSpanRe = new(
        @"<span\s+class=""uid"">(?<uid>[^<]*)</span>",
        RegexOptions.Compiled);

    /// <summary>&lt;title&gt;...&lt;/title&gt; (= スレタイ用)。1 レス目の dat フィールド 5 番目に入る。</summary>
    private static readonly Regex TitleRe = new(
        @"<title>(?<title>.*?)</title>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>本文中の <c>&lt;a href="url" rel="nofollow" target="_blank"&gt;url&lt;/a&gt;</c> を裸 URL に戻す。
    /// 5ch dat の本文 URL は基本生テキスト。HTML 化されていると JS の anchor 化と二重に処理されて壊れる。</summary>
    private static readonly Regex AnchorTagRe = new(
        @"<a\s+[^>]*href=""[^""]*""[^>]*>(?<text>[^<]*)</a>",
        RegexOptions.Compiled);

    /// <summary>jump5.ch のリダイレクタ <c>http://jump5.ch/?https://...</c> から実体 URL を取り出すための置換。</summary>
    private static readonly Regex JumpRedirectRe = new(
        @"https?://jump5?\.ch/\?(?<real>https?://[^\s""<>]+)",
        RegexOptions.Compiled);

    /// <summary>名前から CloudFlare Email Protection link を取り除くマッチャ
    /// (= <c>&lt;a rel="..." href="/cdn-cgi/l/email-protection..."&gt;[内容]&lt;/a&gt;</c>)。
    /// open / close が malformed (= <c>&lt;b&gt;</c> をまたぐ) ことがあり、シンプルに
    /// <c>&lt;a&gt;</c> 開きタグと最後の <c>&lt;/a&gt;</c> 閉じタグだけ削除して中身は維持する。</summary>
    private static readonly Regex AnyAnchorOpenRe  = new(@"<a\s[^>]*>", RegexOptions.Compiled);
    private static readonly Regex AnyAnchorCloseRe = new(@"</a>",       RegexOptions.Compiled);

    /// <summary>5ch.io の <c>read.cgi</c> HTML (Shift_JIS バイト列) を 5ch dat 形式 (Shift_JIS バイト列) に変換する。
    /// パース失敗 (= 1 レスも取れない) のときは null を返す (= 呼び出し元は通常の 404 失敗パスにフォールバック)。</summary>
    public static byte[]? Convert(byte[] shiftJisHtml)
    {
        var sjis = Encoding.GetEncoding("Shift_JIS");
        string html;
        try { html = sjis.GetString(shiftJisHtml); }
        catch { return null; }
        return ConvertText(html);
    }

    /// <summary>UTF-16 文字列としてパース。テスト用 (= byte[] を経由しない経路)。</summary>
    public static byte[]? ConvertText(string html)
    {
        if (string.IsNullOrEmpty(html)) return null;

        // タイトル: <title>...</title>。5ch.io の現状ではスレタイがそのまま入っている (= 末尾の "@5ch掲示板" 等は付かない場合多)。
        var title = TitleRe.Match(html) is { Success: true } tm ? tm.Groups["title"].Value.Trim() : "";

        // 各レスブロックの開始位置をすべて取得
        var opens = PostOpenRe.Matches(html);
        if (opens.Count == 0) return null;

        var sb = new StringBuilder(html.Length);
        var firstPost = true;
        var lastPostNumber = 0;
        var emittedCount = 0;

        for (var i = 0; i < opens.Count; i++)
        {
            var open = opens[i];
            var blockStart = open.Index;
            var blockEnd   = (i + 1 < opens.Count) ? opens[i + 1].Index : html.Length;
            var block      = html.Substring(blockStart, blockEnd - blockStart);

            if (!int.TryParse(open.Groups["num"].Value, out var num)) continue;

            // 連番が抜けている場合 (= あぼーん削除レス等) は dat の前に「うふ～ん」相当のダミーを埋めるべきだが、
            // 現実には DatParser が「行番号 == レス番号」前提で動いているため、ダミー行を挟んで連続性を保つ。
            while (lastPostNumber + 1 < num)
            {
                sb.Append("あぼーん<>あぼーん<>あぼーん ID:DELETED<>あぼーん<>");
                if (firstPost) { sb.Append(title); firstPost = false; }
                sb.Append('\n');
                emittedCount++;
                lastPostNumber++;
            }

            var (name, mail) = ExtractNameAndMail(block);
            var dateAndId    = ExtractDateAndId(block);
            var body         = ExtractBody(block);

            sb.Append(name);
            sb.Append("<>");
            sb.Append(mail);
            sb.Append("<>");
            sb.Append(dateAndId);
            sb.Append("<>");
            sb.Append(body);
            sb.Append("<>");
            if (firstPost) { sb.Append(title); firstPost = false; }
            sb.Append('\n');
            emittedCount++;
            lastPostNumber = num;
        }

        if (emittedCount == 0) return null;

        // SJIS で出せない文字 (= 絵文字等) は数値文字参照に倒して落とす。
        // DatParser 側で WebUtility.HtmlDecode により復元される。
        return HtmlEntityFallbackEncoder.GetBytes(sb.ToString());
    }

    /// <summary>postusername block 内から name (HTML) と mail を抽出する。
    /// 入れ子 <c>&lt;span&gt;</c> 対応 (= 5ch.io は postusername 内に <c>警備員[Lv.X]</c> を span で入れる)、
    /// CloudFlare Email Protection の <c>&lt;a&gt;</c> 残骸を除去、mailto があれば値を mail に出す。 </summary>
    private static (string Name, string Mail) ExtractNameAndMail(string block)
    {
        var inner = ExtractBalanced(block, PostUsernameOpenRe, SpanTagRe);
        if (inner is null) return ("", "");

        var mail = "";
        var mm = MailtoRe.Match(inner);
        if (mm.Success) mail = mm.Groups["mail"].Value;

        // <a> 開きタグと </a> 閉じタグを別々に剥がす。malformed (= <a> が <b> の外で閉じる等) でも
        // とにかく <a> ラッパーを潰せばいい、というスタンス。
        var name = AnyAnchorOpenRe .Replace(inner, "");
        name     = AnyAnchorCloseRe.Replace(name,  "");

        // 末尾 / 先頭の空 <b></b> や空白を清掃
        name = Regex.Replace(name, @"<b>\s*</b>", "");
        name = name.Trim();
        return (name, mail);
    }

    /// <summary>date span と uid span を結合して 「日付 ID:xxx」 形式にする。
    /// どちらかしか取れない場合はあるだけ出す (= dat parser は寛容に解釈する)。</summary>
    private static string ExtractDateAndId(string block)
    {
        var date = DateSpanRe.Match(block) is { Success: true } dm ? dm.Groups["date"].Value.Trim() : "";
        var uid  = UidSpanRe .Match(block) is { Success: true } um ? um.Groups["uid"] .Value.Trim() : "";
        if (string.IsNullOrEmpty(date)) return uid;
        if (string.IsNullOrEmpty(uid))  return date;
        return $"{date} {uid}";
    }

    /// <summary>post-content block から本文を抽出。入れ子 <c>&lt;div&gt;</c> 対応 (= レアだが念のため)、
    /// <c>&lt;a href="..."&gt;url&lt;/a&gt;</c> 形式のリンクは裸 URL に戻し (= dat 仕様)、
    /// jump5.ch リダイレクタを介している URL は実体側を採用。 </summary>
    private static string ExtractBody(string block)
    {
        var body = ExtractBalanced(block, PostContentOpenRe, DivTagRe);
        if (body is null) return "";

        body = AnchorTagRe.Replace(body, m =>
        {
            var text = m.Groups["text"].Value;
            // visible テキストが空 (= 画像 a タグや CDN Email Protection 等) の場合は href を吐くより無視する方が安全。
            return string.IsNullOrEmpty(text) ? "" : text;
        });

        body = JumpRedirectRe.Replace(body, m => m.Groups["real"].Value);

        return body.Trim();
    }

    /// <summary>指定の open タグ regex で <paramref name="html"/> 内の最初の出現を見つけ、
    /// その後の同種タグ (= span / div) の入れ子を <paramref name="anyTagRe"/> で balance 取りながら
    /// 対応する閉じタグを特定し、open の終わりから close の手前までの content を返す。
    /// 見つからない / unmatched なら null。 </summary>
    private static string? ExtractBalanced(string html, Regex openRe, Regex anyTagRe)
    {
        var open = openRe.Match(html);
        if (!open.Success) return null;
        var contentStart = open.Index + open.Length;
        var depth = 1;
        var pos   = contentStart;
        while (depth > 0)
        {
            var m = anyTagRe.Match(html, pos);
            if (!m.Success) return null;
            if (m.Value.StartsWith("</")) { depth--; if (depth == 0) return html.Substring(contentStart, m.Index - contentStart); }
            else                           { depth++; }
            pos = m.Index + m.Length;
        }
        return null;
    }
}

/// <summary>
/// <see cref="HtmlToDatConverter"/> 内で使う Shift_JIS エンコーダ (= SJIS で表現できない Unicode 文字を
/// HTML 数値文字参照 <c>&amp;#xNNNN;</c> にフォールバック)。
/// dat の歴史的慣習: SJIS にない文字は数値参照で送られてくる。<see cref="DatParser"/> は <c>WebUtility.HtmlDecode</c>
/// でこれを復元するので、エンコーダ側がそれを生成すれば往復で問題ない。
/// </summary>
internal static class HtmlEntityFallbackEncoder
{
    /// <summary>SJIS エンコード時、表現できない Unicode コードポイントを <c>&amp;#xNNNN;</c> に書き換えるフォールバック実装。</summary>
    private sealed class HtmlEntityFallback : EncoderFallback
    {
        public override int MaxCharCount => 16; // "&#x10FFFF;" でも 10 文字、surrogate pair 考慮で 16 で十分
        public override EncoderFallbackBuffer CreateFallbackBuffer() => new HtmlEntityFallbackBuffer();
    }

    private sealed class HtmlEntityFallbackBuffer : EncoderFallbackBuffer
    {
        private string _pending = "";
        private int    _index;

        public override int Remaining => _pending.Length - _index;

        public override bool Fallback(char c, int index)
        {
            _pending = $"&#x{(int)c:X};";
            _index   = 0;
            return true;
        }

        public override bool Fallback(char high, char low, int index)
        {
            var cp = char.ConvertToUtf32(high, low);
            _pending = $"&#x{cp:X};";
            _index   = 0;
            return true;
        }

        public override char GetNextChar()
        {
            if (_index >= _pending.Length) return '\0';
            return _pending[_index++];
        }

        public override bool MovePrevious()
        {
            if (_index == 0) return false;
            _index--;
            return true;
        }

        public override void Reset()
        {
            _pending = "";
            _index   = 0;
        }
    }

    /// <summary>UTF-16 文字列を Shift_JIS バイト列に変換 (= SJIS にない文字は <c>&amp;#xNNNN;</c> 参照に倒す)。</summary>
    public static byte[] GetBytes(string text)
    {
        var sjis = (Encoding)Encoding.GetEncoding("Shift_JIS").Clone();
        sjis.EncoderFallback = new HtmlEntityFallback();
        return sjis.GetBytes(text);
    }
}
