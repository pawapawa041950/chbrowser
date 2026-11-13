using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChBrowser.Services.Image;

/// <summary>サムネイルの取得ルール 1 つの、ファイルに書いてあるとおりの内容 (検査前。設定画面の編集とファイルの読み書きに使う)。
/// 書式の説明は既定のルール (<c>Resources/thumbnail-rules.json</c>) の先頭にある。</summary>
public sealed record ThumbnailRuleData
{
    public string  Name     { get; init; } = "";
    public bool    Enabled  { get; init; } = true;
    public string  Match    { get; init; } = "";
    /// <summary>取得せずに URL を組み立てるだけのとき、その URL のひな形 (空でなければ <see cref="FetchUrl"/> より優先)。</summary>
    public string? Result   { get; init; }
    public string? FetchUrl { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    /// <summary>"json" か "regex"。</summary>
    public string  Format   { get; init; } = "json";
    public IReadOnlyList<string> Extract { get; init; } = Array.Empty<string>();
    /// <summary>"unavailable" か "noMedia"。</summary>
    public string  Empty    { get; init; } = "unavailable";

    /// <summary>内容を検査して、使える形 (<see cref="ThumbnailRule"/>) にする。書き間違いは <see cref="FormatException"/> (利用者に見せる文面)。</summary>
    public ThumbnailRule Compile()
    {
        if (string.IsNullOrWhiteSpace(Match)) throw new FormatException("対象 URL (match) がありません");
        Regex regex;
        try { regex = new Regex(Match, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)); }
        catch (ArgumentException ex) { throw new FormatException("対象 URL (match) の正規表現が正しくありません: " + ex.Message); }

        var result   = string.IsNullOrWhiteSpace(Result)   ? null : Result!.Trim();
        var fetchUrl = string.IsNullOrWhiteSpace(FetchUrl) ? null : FetchUrl!.Trim();
        if (result is null && fetchUrl is null) throw new FormatException("組み立てる URL (result) か取得先の URL (fetch.url) のどちらかが要ります");

        var format = (Format ?? "json").Trim().ToLowerInvariant();
        if (format != "json" && format != "regex") throw new FormatException($"読み方 (format) \"{Format}\" は使えません (json / regex)");

        var extract = Extract.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList();
        if (result is null && extract.Count == 0) throw new FormatException("取り出し方 (extract) がありません");
        if (result is null && format == "regex")
        {
            foreach (var x in extract)
            {
                try { _ = new Regex(x); }
                catch (ArgumentException ex) { throw new FormatException($"取り出し方 (extract) の正規表現が正しくありません: {x} ({ex.Message})"); }
            }
        }

        return new ThumbnailRule
        {
            Name              = string.IsNullOrWhiteSpace(Name) ? "(名前なし)" : Name.Trim(),
            Match             = Match,
            Regex             = regex,
            Result            = result,
            FetchUrl          = fetchUrl,
            FetchHeaders      = new Dictionary<string, string>(Headers, StringComparer.OrdinalIgnoreCase),
            UseRegex          = format == "regex",
            Extract           = extract,
            EmptyMeansNoMedia = string.Equals((Empty ?? "").Trim(), "noMedia", StringComparison.OrdinalIgnoreCase),
        };
    }
}

/// <summary>サムネイルの取得ルール 1 つ (検査済み): 画像・動画そのものではないページの URL (x.com の投稿、imgur のアルバム等) から、
/// サムネイルにする画像の URL を求める手順。</summary>
public sealed class ThumbnailRule
{
    public required string Name  { get; init; }
    public required string Match { get; init; }
    public required Regex  Regex { get; init; }

    /// <summary>取得せずに URL を組み立てるだけのとき、その URL のひな形。null なら <see cref="FetchUrl"/> を取得する。</summary>
    public string? Result { get; init; }

    public string? FetchUrl { get; init; }
    public IReadOnlyDictionary<string, string> FetchHeaders { get; init; } = new Dictionary<string, string>();

    /// <summary>取得した内容を正規表現で読むか (false = JSON)。</summary>
    public bool UseRegex { get; init; }

    /// <summary>取り出し方の候補 (上から試し、1 件以上取れた最初のもの)。</summary>
    public IReadOnlyList<string> Extract { get; init; } = Array.Empty<string>();

    /// <summary>何も取れなかったとき「画像が無い」と確定させるか (false = 取得失敗の扱い)。</summary>
    public bool EmptyMeansNoMedia { get; init; }

    /// <summary>ひな形の <c>{名前}</c> / <c>{番号}</c> を、<paramref name="m"/> のグループの値 (URL 用にエスケープ) で埋める。</summary>
    public static string Fill(string template, Match m)
        => Regex.Replace(template, @"\{([A-Za-z0-9_]+)\}", t =>
        {
            var g = m.Groups[t.Groups[1].Value];
            return g.Success ? Uri.EscapeDataString(g.Value) : t.Value;
        });
}

/// <summary>サムネイルの取得ルールの一覧 (利用者のルール → 既定のルールの順。最初に一致した 1 つを使う)。</summary>
public sealed class ThumbnailRuleSet
{
    /// <summary>利用者のルールのファイル名 (データフォルダの app の下)。</summary>
    public const string UserFileName = "thumbnail_rules.json";

    /// <summary>今使っている一覧 (起動時と設定画面での変更時に入れ替える)。スレ表示が対象 URL の一覧を表示側へ渡すのに使う。</summary>
    public static ThumbnailRuleSet Current { get; set; } = new(Array.Empty<ThumbnailRule>());

    public IReadOnlyList<ThumbnailRule> Rules { get; }

    public ThumbnailRuleSet(IReadOnlyList<ThumbnailRule> rules) => Rules = rules;

    /// <summary>表示側 (thread.js) に渡す、対象 URL の正規表現の一覧。</summary>
    public string[] JsPatterns => Rules.Select(r => r.Match).ToArray();

    public bool IsMatch(string url) => Find(url) is not null;

    public (ThumbnailRule Rule, Match Match)? Find(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        foreach (var r in Rules)
        {
            Match m;
            try { m = r.Regex.Match(url); }
            catch (RegexMatchTimeoutException) { continue; }
            if (m.Success) return (r, m);
        }
        return null;
    }

    /// <summary>既定のルール (埋め込み) と利用者のルール (<paramref name="userPath"/>。無ければ既定だけ) を読む。
    /// 読めないルールは飛ばしてログに書く (1 つの書き間違いで全部が止まらないように)。</summary>
    public static ThumbnailRuleSet Load(string builtinJson, string? userPath)
    {
        var log = ChBrowser.Services.Logging.LogService.Instance;
        var user = new List<ThumbnailRuleData>();
        if (userPath is not null && File.Exists(userPath))
        {
            try { user = ReadData(File.ReadAllText(userPath)); }
            catch (Exception ex) { log.Write($"[ThumbRule] {UserFileName} を読めません: {ex.Message}"); }
        }
        List<ThumbnailRuleData> builtin;
        try { builtin = ReadData(builtinJson); }
        catch (Exception ex) { log.Write($"[ThumbRule] 既定のルールを読めません: {ex.Message}"); builtin = new(); }

        var set = Build(user, builtin, log.Write);
        if (user.Count > 0) log.Write($"[ThumbRule] 利用者のルール {user.Count} 件を読み込みました (有効なルールは合計 {set.Rules.Count} 件)");
        return set;
    }

    /// <summary>利用者のルール → 既定のルールの順に並べた一覧を作る。同じ名前の利用者のルールは既定を置き換え、
    /// 止めてある (<see cref="ThumbnailRuleData.Enabled"/> = false) ルールは使わない (同じ名前の既定も止まる)。</summary>
    public static ThumbnailRuleSet Build(IReadOnlyList<ThumbnailRuleData> user, IReadOnlyList<ThumbnailRuleData> builtin, Action<string>? log = null)
    {
        var rules = new List<ThumbnailRule>();
        var userNames = new HashSet<string>(user.Select(u => u.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var (d, label) in user.Select(u => (u, "利用者のルール"))
                     .Concat(builtin.Where(b => !userNames.Contains(b.Name)).Select(b => (b, "既定のルール"))))
        {
            if (!d.Enabled) continue;
            try { rules.Add(d.Compile()); }
            catch (FormatException ex) { log?.Invoke($"[ThumbRule] {label}「{d.Name}」を読めないので飛ばします: {ex.Message}"); }
        }
        return new ThumbnailRuleSet(rules);
    }

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling     = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>ルールのファイルを、書いてあるとおりに読む (検査はしない)。ファイル自体が JSON として読めなければ例外。</summary>
    public static List<ThumbnailRuleData> ReadData(string json)
    {
        var list = new List<ThumbnailRuleData>();
        using var doc = JsonDocument.Parse(json, ParseOptions);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
            throw new FormatException("\"rules\" の配列がありません");

        var i = 0;
        foreach (var e in rules.EnumerateArray())
        {
            i++;
            if (e.ValueKind != JsonValueKind.Object) continue;

            string? fetchUrl = null;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (e.TryGetProperty("fetch", out var f) && f.ValueKind == JsonValueKind.Object)
            {
                fetchUrl = Str(f, "url");
                if (f.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
                    foreach (var p in h.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.String) headers[p.Name] = p.Value.GetString()!;
            }
            var extract = new List<string>();
            if (e.TryGetProperty("extract", out var ex))
            {
                if (ex.ValueKind == JsonValueKind.String) extract.Add(ex.GetString()!);
                else if (ex.ValueKind == JsonValueKind.Array)
                    extract.AddRange(ex.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
            }
            list.Add(new ThumbnailRuleData
            {
                Name     = Str(e, "name") ?? $"#{i}",
                Enabled  = !(e.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False),
                Match    = Str(e, "match") ?? "",
                Result   = Str(e, "result"),
                FetchUrl = fetchUrl,
                Headers  = headers,
                Format   = Str(e, "format") ?? "json",
                Extract  = extract,
                Empty    = Str(e, "empty") ?? "unavailable",
            });
        }
        return list;
    }

    /// <summary>ルールの一覧をファイルの書式 (JSON) にする (設定画面からの保存用)。</summary>
    public static string WriteData(IEnumerable<ThumbnailRuleData> rules)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteStartArray("rules");
            foreach (var d in rules)
            {
                w.WriteStartObject();
                w.WriteString("name", d.Name);
                if (!d.Enabled) w.WriteBoolean("enabled", false);
                w.WriteString("match", d.Match);
                if (!string.IsNullOrWhiteSpace(d.Result)) w.WriteString("result", d.Result);
                if (!string.IsNullOrWhiteSpace(d.FetchUrl) || d.Headers.Count > 0)
                {
                    w.WriteStartObject("fetch");
                    w.WriteString("url", d.FetchUrl ?? "");
                    if (d.Headers.Count > 0)
                    {
                        w.WriteStartObject("headers");
                        foreach (var (k, v) in d.Headers) w.WriteString(k, v);
                        w.WriteEndObject();
                    }
                    w.WriteEndObject();
                }
                w.WriteString("format", d.Format);
                if (d.Extract.Count > 0)
                {
                    w.WriteStartArray("extract");
                    foreach (var x in d.Extract) w.WriteStringValue(x);
                    w.WriteEndArray();
                }
                if (!string.Equals(d.Empty, "unavailable", StringComparison.OrdinalIgnoreCase)) w.WriteString("empty", d.Empty);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    // ---------------------------------------------------------------
    // 取り出し
    // ---------------------------------------------------------------

    /// <summary>取得した内容 <paramref name="content"/> から、ルールの取り出し方で URL を取り出す (候補を上から試し、取れた最初のもの)。
    /// 相対 URL は <paramref name="baseUrl"/> から解決し、http / https だけを重複なしで返す。</summary>
    public static List<string> ExtractUrls(ThumbnailRule rule, string content, Uri baseUrl)
    {
        JsonDocument? doc = null;
        try
        {
            if (!rule.UseRegex) doc = JsonDocument.Parse(content);
            foreach (var x in rule.Extract)
            {
                var raw = rule.UseRegex ? ByRegex(content, x) : ByJsonPath(doc!.RootElement, x);
                var urls = Normalize(raw, baseUrl);
                if (urls.Count > 0) return urls;
            }
            return new List<string>();
        }
        finally { doc?.Dispose(); }
    }

    /// <summary>取り出す URL の上限 (アルバムが極端に大きいときの歯止め)。</summary>
    private const int MaxUrls = 200;

    private static List<string> Normalize(IEnumerable<string> raw, Uri baseUrl)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var s in raw)
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            if (!Uri.TryCreate(baseUrl, s.Trim(), out var u)) continue;
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) continue;
            if (seen.Add(u.AbsoluteUri)) list.Add(u.AbsoluteUri);
            if (list.Count >= MaxUrls) break;
        }
        return list;
    }

    private static IEnumerable<string> ByRegex(string content, string pattern)
    {
        var re = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        foreach (Match m in re.Matches(content))
            yield return WebUtility.HtmlDecode(m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : m.Value);
    }

    /// <summary>JSON の項目の場所 (<c>a.b[].c|d</c>) をたどって文字列の値を集める。
    /// <c>[]</c> = 配列の全要素、<c>[n]</c> = n 番目、<c>a|b</c> = a が無ければ b (要素ごとに判定)。</summary>
    internal static List<string> ByJsonPath(JsonElement root, string path)
    {
        var current = new List<JsonElement> { root };
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var seg = segment.Trim();
            // 末尾の [] / [n] を外す (複数重ねられる: a[][0])
            var indexers = new List<int?>();
            while (seg.EndsWith(']'))
            {
                var open = seg.LastIndexOf('[');
                if (open < 0) break;
                var inner = seg.Substring(open + 1, seg.Length - open - 2).Trim();
                indexers.Insert(0, inner.Length == 0 ? null : int.TryParse(inner, out var n) ? n : -1);
                seg = seg.Substring(0, open);
            }
            var names = seg.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var next = new List<JsonElement>();
            foreach (var el in current)
            {
                var v = el;
                if (names.Length > 0)
                {
                    if (el.ValueKind != JsonValueKind.Object) continue;
                    var found = false;
                    foreach (var name in names)
                    {
                        if (el.TryGetProperty(name, out var p) && p.ValueKind != JsonValueKind.Null &&
                            !(p.ValueKind == JsonValueKind.String && p.GetString()!.Length == 0))
                        { v = p; found = true; break; }
                    }
                    if (!found) continue;
                }
                var items = new List<JsonElement> { v };
                foreach (var ix in indexers)
                {
                    var expanded = new List<JsonElement>();
                    foreach (var it in items)
                    {
                        if (it.ValueKind != JsonValueKind.Array) continue;
                        if (ix is null) expanded.AddRange(it.EnumerateArray());
                        else if (ix >= 0 && ix < it.GetArrayLength()) expanded.Add(it[ix.Value]);
                    }
                    items = expanded;
                }
                next.AddRange(items);
            }
            current = next;
            if (current.Count == 0) break;
        }
        return current.Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
    }
}
