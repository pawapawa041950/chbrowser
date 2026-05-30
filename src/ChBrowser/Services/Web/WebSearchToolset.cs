using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Services.Llm;

namespace ChBrowser.Services.Web;

/// <summary>WEB 検索 (DuckDuckGo) とページ本文取得を提供する公開ツールセット。
///
/// <para><see cref="ToolCatalog"/> 経由で内蔵エージェント (Worker) と MCP サーバの両方に公開される
/// (= スレ系ツールと同じ土台)。バックエンドはひとまず <b>DuckDuckGo の HTML エンドポイント</b>
/// (<c>html.duckduckgo.com/html/</c>) をパースする方式 (API キー不要)。非公式なので HTML 変更や
/// レート制限で失敗しうる点に注意 (= 失敗時は <c>{"error":...}</c> を返す)。将来 Brave 等への差し替えは
/// この 1 クラスを置き換えるだけでよい (ツール表面 web_search / web_fetch は固定)。</para>
///
/// <para>公開ツール:
/// <list type="bullet">
///   <item><c>web_search(query, count?)</c> — 検索しタイトル / URL / 抜粋の一覧を返す。</item>
///   <item><c>web_fetch(url, max_chars?)</c> — ページを取得して本文テキストを抽出して返す。</item>
/// </list></para></summary>
public sealed class WebSearchToolset : IAgentToolset
{
    private const int DefaultSearchCount = 6;
    private const int MaxSearchCount     = 15;
    private const int DefaultFetchChars  = 4000;
    private const int MinFetchChars      = 500;
    private const int MaxFetchChars      = 20000;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>検索 / 取得共用の HttpClient。SocketsHttpHandler でリダイレクト追従 + 自動展開。
    /// インスタンスはツール呼び出しごとに使い捨てなので、HttpClient は static 共有でソケットを使い回す。</summary>
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect      = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        };
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ja,en;q=0.8");
        return http;
    }

    // ---- IAgentToolset ----

    public IReadOnlyList<object> GetToolDefinitions() => new object[]
    {
        new
        {
            type     = "function",
            function = new
            {
                name        = "web_search",
                description = "DuckDuckGo で WEB 検索し、ヒットしたページのタイトル / URL / 抜粋の一覧を返す。" +
                              "5ch の外の最新情報・事実確認・用語や固有名詞の裏取りなどに使う。" +
                              "結果は抜粋までなので、本文が必要なら返ってきた url を web_fetch に渡して読むこと。",
                parameters  = new
                {
                    type       = "object",
                    properties = new
                    {
                        query = new { type = "string",  description = "検索クエリ (自然文 / キーワード)。" },
                        count = new { type = "integer", description = $"返す最大件数 (既定 {DefaultSearchCount}, 上限 {MaxSearchCount})。" },
                    },
                    required = new[] { "query" },
                },
            },
        },
        new
        {
            type     = "function",
            function = new
            {
                name        = "web_fetch",
                description = "指定 URL のページを取得して本文テキストを抽出して返す (HTML はタグ除去・整形済み)。" +
                              "web_search で見つけた url の中身を読むときに使う。長いページは max_chars で切り詰める。",
                parameters  = new
                {
                    type       = "object",
                    properties = new
                    {
                        url       = new { type = "string",  description = "取得する http / https の URL。" },
                        max_chars = new { type = "integer", description = $"返す本文の最大文字数 (既定 {DefaultFetchChars}, {MinFetchChars}〜{MaxFetchChars})。" },
                    },
                    required = new[] { "url" },
                },
            },
        },
    };

    public Task<string> ExecuteAsync(string name, string argumentsJson, CancellationToken ct = default) => name switch
    {
        "web_search" => WebSearchAsync(argumentsJson, ct),
        "web_fetch"  => WebFetchAsync(argumentsJson, ct),
        _            => Task.FromResult(ErrorJson($"未知のツール: {name}")),
    };

    // ---- web_search ----

    private async Task<string> WebSearchAsync(string argsJson, CancellationToken ct)
    {
        if (!TryParseObject(argsJson, out var args))
            return ErrorJson("引数 JSON のパースに失敗");
        var query = TryStr(args, "query");
        if (string.IsNullOrWhiteSpace(query))
            return ErrorJson("query が指定されていません");
        var count = DefaultSearchCount;
        if (args.TryGetProperty("count", out var cEl) && TryGetIntLoose(cEl, out var c))
            count = Math.Clamp(c, 1, MaxSearchCount);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeout);

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://html.duckduckgo.com/html/")
            {
                Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("q", query!) }),
            };
            using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(true);
            if (!resp.IsSuccessStatusCode)
                return ErrorJson($"検索リクエスト失敗: HTTP {(int)resp.StatusCode}");
            var html = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(true);

            var results = ParseDuckDuckGoHtml(html, count);
            return JsonSerializer.Serialize(new
            {
                query,
                count    = results.Count,
                results,
            }, JsonOpts);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ErrorJson("検索がタイムアウトしました");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return ErrorJson($"検索で例外: {ex.Message}");
        }
    }

    private static readonly Regex ResultAnchorRe = new(
        @"<a\b([^>]*\bclass=""[^""]*\bresult__a\b[^""]*""[^>]*)>([\s\S]*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SnippetRe = new(
        @"<a\b[^>]*\bclass=""[^""]*\bresult__snippet\b[^""]*""[^>]*>([\s\S]*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HrefRe = new(@"href=""([^""]*)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UddgRe = new(@"[?&]uddg=([^&]+)", RegexOptions.Compiled);

    /// <summary>DuckDuckGo html エンドポイントの結果 HTML から (title, url, snippet) を抽出する。
    /// 広告 (duckduckgo.com/y.js) は除外し、各結果アンカーの直後に現れる snippet を本文として対応付ける
    /// (= 広告でズレないよう、別リストの index zip ではなく文書位置で対応させる)。</summary>
    private static List<object> ParseDuckDuckGoHtml(string html, int count)
    {
        var snippetMatches = SnippetRe.Matches(html);
        var results = new List<object>(count);

        foreach (Match m in ResultAnchorRe.Matches(html))
        {
            var attrs = m.Groups[1].Value;
            var hrefM = HrefRe.Match(attrs);
            if (!hrefM.Success) continue;
            var url   = ResolveUrl(hrefM.Groups[1].Value);
            var title = CleanText(m.Groups[2].Value);
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(title)) continue;
            if (IsAdUrl(url)) continue;   // 広告を除外

            // このアンカーの直後 (= 文書位置がより後) に来る最初の snippet を採用。
            var snippet = "";
            foreach (Match sm in snippetMatches)
            {
                if (sm.Index > m.Index) { snippet = CleanText(sm.Groups[1].Value); break; }
            }

            results.Add(new { title, url, snippet });
            if (results.Count >= count) break;
        }
        return results;
    }

    /// <summary>DDG の広告リンク (= 実 URL に解せず duckduckgo.com 配下の y.js トラッキングに留まるもの) か。</summary>
    private static bool IsAdUrl(string url)
    {
        if (url.Contains("duckduckgo.com/y.js", StringComparison.OrdinalIgnoreCase)) return true;
        if (Uri.TryCreate(url, UriKind.Absolute, out var u)
            && u.Host.Equals("duckduckgo.com", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>DDG のリダイレクト URL (<c>//duckduckgo.com/l/?uddg=...</c>) を実 URL に解す。</summary>
    private static string ResolveUrl(string href)
    {
        href = WebUtility.HtmlDecode(href ?? "");
        var m = UddgRe.Match(href);
        if (m.Success)
        {
            try { return Uri.UnescapeDataString(m.Groups[1].Value); } catch { /* fallthrough */ }
        }
        if (href.StartsWith("//", StringComparison.Ordinal)) return "https:" + href;
        return href;
    }

    // ---- web_fetch ----

    private async Task<string> WebFetchAsync(string argsJson, CancellationToken ct)
    {
        if (!TryParseObject(argsJson, out var args))
            return ErrorJson("引数 JSON のパースに失敗");
        var url = TryStr(args, "url");
        if (string.IsNullOrWhiteSpace(url))
            return ErrorJson("url が指定されていません");
        var maxChars = DefaultFetchChars;
        if (args.TryGetProperty("max_chars", out var mEl) && TryGetIntLoose(mEl, out var mc))
            maxChars = Math.Clamp(mc, MinFetchChars, MaxFetchChars);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return ErrorJson($"url の形式が不正です: {url}");
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return ErrorJson("http / https の URL のみ取得できます");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeout);

            // SSRF 対策: 内部 / ループバック / プライベートアドレス宛ては拒否 (MCP で外部に公開されるため)。
            if (await IsBlockedTargetAsync(uri, cts.Token).ConfigureAwait(true))
                return ErrorJson("内部 / プライベートネットワーク宛ての取得は許可されていません");

            using var resp = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(true);
            var finalUrl    = resp.RequestMessage?.RequestUri?.ToString() ?? url;
            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";

            if (!resp.IsSuccessStatusCode)
                return ErrorJson($"取得失敗: HTTP {(int)resp.StatusCode} ({finalUrl})");

            var isText = contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                      || contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
                      || contentType.Contains("xml",  StringComparison.OrdinalIgnoreCase)
                      || contentType.Contains("json", StringComparison.OrdinalIgnoreCase);
            if (!isText)
                return JsonSerializer.Serialize(new
                {
                    url, final_url = finalUrl, content_type = contentType,
                    note = "テキスト系でないコンテンツのため本文抽出はスキップしました。",
                }, JsonOpts);

            var raw = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(true);
            var isHtml = contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
                      || raw.IndexOf("<html", StringComparison.OrdinalIgnoreCase) >= 0;

            string? title = null;
            string text;
            if (isHtml)
            {
                title = ExtractTitle(raw);
                text  = ExtractTextFromHtml(raw);
            }
            else
            {
                text = raw.Trim();
            }

            var truncated = text.Length > maxChars;
            if (truncated) text = text.Substring(0, maxChars) + "…";

            return JsonSerializer.Serialize(new
            {
                url,
                final_url    = finalUrl,
                content_type = contentType,
                title,
                truncated,
                text,
            }, JsonOpts);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ErrorJson("ページ取得がタイムアウトしました");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return ErrorJson($"ページ取得で例外: {ex.Message}");
        }
    }

    /// <summary>取得先が内部 / ループバック / プライベート / リンクローカルなら true (= 拒否)。
    /// ホスト名は DNS 解決して解決先 IP も検査する (= rebinding 的な悪用をある程度防ぐ)。</summary>
    private static async Task<bool> IsBlockedTargetAsync(Uri uri, CancellationToken ct)
    {
        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;

        if (IPAddress.TryParse(host, out var literal))
            return IsPrivateOrLoopback(literal);

        try
        {
            var addrs = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(true);
            foreach (var a in addrs)
                if (IsPrivateOrLoopback(a)) return true;
        }
        catch
        {
            // 解決失敗時はブロックしない (= 実リクエスト側でエラーになる)。
        }
        return false;
    }

    private static bool IsPrivateOrLoopback(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 10) return true;                            // 10.0.0.0/8
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true; // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return true;            // 192.168.0.0/16
            if (b[0] == 169 && b[1] == 254) return true;            // 169.254.0.0/16 link-local
            if (b[0] == 127) return true;                           // loopback
            if (b[0] == 0) return true;
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true;                 // fc00::/7 unique-local
        }
        return false;
    }

    private static readonly Regex ScriptRe  = new(@"<script[\s\S]*?</script>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StyleRe   = new(@"<style[\s\S]*?</style>",   RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NoscriptRe= new(@"<noscript[\s\S]*?</noscript>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BlockEndRe= new(@"(?i)</(p|div|li|h[1-6]|tr|section|article|ul|ol|table|blockquote)>", RegexOptions.Compiled);
    private static readonly Regex BrRe      = new(@"(?i)<br\s*/?>", RegexOptions.Compiled);
    private static readonly Regex TagRe     = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex TitleRe   = new(@"<title[^>]*>([\s\S]*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string? ExtractTitle(string html)
    {
        var m = TitleRe.Match(html);
        return m.Success ? CleanText(m.Groups[1].Value) : null;
    }

    /// <summary>HTML から本文テキストを抽出する (script/style 除去 → ブロック要素を改行化 → タグ除去 → 整形)。</summary>
    private static string ExtractTextFromHtml(string html)
    {
        html = ScriptRe.Replace(html, " ");
        html = StyleRe.Replace(html, " ");
        html = NoscriptRe.Replace(html, " ");
        html = BrRe.Replace(html, "\n");
        html = BlockEndRe.Replace(html, "\n");
        var text = TagRe.Replace(html, " ");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"[ \t\f\v ]+", " ");
        text = Regex.Replace(text, @" *\n *", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    // ---- helpers ----

    private static string CleanText(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var t = TagRe.Replace(s, "");
        t = WebUtility.HtmlDecode(t);
        return Regex.Replace(t, @"\s+", " ").Trim();
    }

    private static bool TryParseObject(string json, out JsonElement obj)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            obj = doc.RootElement.Clone();
            return obj.ValueKind == JsonValueKind.Object;
        }
        catch { obj = default; return false; }
    }

    private static string? TryStr(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static bool TryGetIntLoose(JsonElement el, out int value)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Number: return el.TryGetInt32(out value);
            case JsonValueKind.String: return int.TryParse(el.GetString(), out value);
            default: value = 0; return false;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string ErrorJson(string message)
        => JsonSerializer.Serialize(new { error = message }, JsonOpts);
}
