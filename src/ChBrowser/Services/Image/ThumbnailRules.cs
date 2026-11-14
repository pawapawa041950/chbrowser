using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChBrowser.Services.Image;

/// <summary>サムネイルルール 1 つの、ファイルに書いてあるとおりの内容 (検査前。設定画面の編集とファイルの読み書きに使う)。
/// 書式の説明は既定のルール (<c>Resources/thumbnail-rules.csv</c>) の先頭にある。</summary>
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
    /// <summary>取り出した URL の出し方: "auto" (拡張子で画像か動画かを決める) / "image" (サムネイル) / "video" (動画プレーヤー) /
    /// "external" (サムネイルに再生マークを重ね、クリックで元の URL を既定のブラウザで開く)。</summary>
    public string  Display  { get; init; } = "auto";
    /// <summary>ファイルでこのルールの直前に書いてあったコメント行 (# で始まる行・空行。書いてあるとおり)。保存のときにそのまま書き戻す。</summary>
    public string  Comment  { get; init; } = "";

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

        var display = (Display ?? "").Trim().ToLowerInvariant();
        if (display.Length == 0) display = ThumbnailRule.DisplayAuto;
        if (display is not (ThumbnailRule.DisplayAuto or ThumbnailRule.DisplayImage or ThumbnailRule.DisplayVideo or ThumbnailRule.DisplayExternal))
            throw new FormatException($"表示のしかた (display) \"{Display}\" は使えません (auto / image / video / external)");

        return new ThumbnailRule
        {
            Display           = display,
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

/// <summary>ルールのファイル 1 つ分: ルールと、ルールに付かないコメント行。</summary>
/// <param name="Rules">ルール (直前のコメント行は各ルールの <see cref="ThumbnailRuleData.Comment"/>)。</param>
/// <param name="Preamble">見出し行の前のコメント行 (見出し行が無いファイルでは null)。</param>
/// <param name="Trailer">最後のルールより後のコメント行。</param>
public sealed record ThumbnailRuleFile(List<ThumbnailRuleData> Rules, string? Preamble, string Trailer);

/// <summary>サムネイルルール 1 つ (検査済み): 画像・動画そのものではないページの URL (x.com の投稿、imgur のアルバム等) から、
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

    public const string DisplayAuto     = "auto";
    public const string DisplayImage    = "image";
    public const string DisplayVideo    = "video";
    public const string DisplayExternal = "external";

    /// <summary>取り出した URL の出し方 (<see cref="DisplayAuto"/> 等)。</summary>
    public string Display { get; init; } = DisplayAuto;

    /// <summary>ひな形の <c>{名前}</c> / <c>{番号}</c> を、<paramref name="m"/> のグループの値 (URL 用にエスケープ) で埋める。</summary>
    public static string Fill(string template, Match m)
        => Regex.Replace(template, @"\{([A-Za-z0-9_]+)\}", t =>
        {
            var g = m.Groups[t.Groups[1].Value];
            return g.Success ? Uri.EscapeDataString(g.Value) : t.Value;
        });
}

/// <summary>サムネイルルールの一覧 (利用者のルール → 既定のルールの順。最初に一致した 1 つを使う)。</summary>
public sealed class ThumbnailRuleSet
{
    /// <summary>利用者のルールのファイル名 (データフォルダの app の下)。</summary>
    public const string UserFileName = "thumbnail_rules.csv";

    /// <summary>今使っている一覧 (起動時と設定画面での変更時に入れ替える)。スレ表示が対象 URL の一覧を表示側へ渡すのに使う。</summary>
    public static ThumbnailRuleSet Current { get; set; } = new(Array.Empty<ThumbnailRule>());

    public IReadOnlyList<ThumbnailRule> Rules { get; }

    public ThumbnailRuleSet(IReadOnlyList<ThumbnailRule> rules) => Rules = rules;

    /// <summary>表示側 (thread.js) に渡すルールの一覧 (上から順): 対象 URL の正規表現・表示のしかた・
    /// 取得せずに URL を組み立てるだけのルールならそのひな形 (表示側がその場で組み立てる。null = アプリに問い合わせる)。</summary>
    public object[] JsRules => Rules.Select(r => (object)new { match = r.Match, result = r.Result, display = r.Display }).ToArray();

    /// <summary><see cref="JsRules"/> を JSON にしたもの (表示側へ渡す形。ルールが変わったかの比較にも使う)。</summary>
    public string JsRulesJson => System.Text.Json.JsonSerializer.Serialize(JsRules);

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
    public static ThumbnailRuleSet Load(string builtinCsv, string? userPath)
    {
        var log = ChBrowser.Services.Logging.LogService.Instance;
        var user = new List<ThumbnailRuleData>();
        if (userPath is not null && File.Exists(userPath))
        {
            try { user = ReadData(File.ReadAllText(userPath)); }
            catch (Exception ex) { log.Write($"[ThumbRule] {UserFileName} を読めません: {ex.Message}"); }
        }
        List<ThumbnailRuleData> builtin;
        try { builtin = ReadData(builtinCsv); }
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

    // ---------------------------------------------------------------
    // ファイル (CSV。1 行 = 1 ルール)
    // ---------------------------------------------------------------

    /// <summary>列の並び (見出し行)。ファイルに見出し行があればその並びで読む (列を入れ替えてあっても読める)。</summary>
    private static readonly string[] Columns = { "enabled", "name", "match", "result", "fetch_url", "headers", "format", "extract", "empty", "display" };

    /// <summary>1 つの項目に複数の値 (ヘッダー・取り出し方の候補) を書くときの区切り。</summary>
    private const string ListSeparator = "||";

    /// <summary>ルールのファイル (CSV) を、書いてあるとおりに読む (検査はしない)。# で始まる行と空行はルールではない。</summary>
    public static List<ThumbnailRuleData> ReadData(string csv) => ReadFile(csv).Rules;

    /// <summary>ルールのファイル (CSV) を、コメント行 (# で始まる行・空行) ごと読む。コメント行は、直後のルール
    /// (<see cref="ThumbnailRuleData.Comment"/>)・見出し行の前 (<see cref="ThumbnailRuleFile.Preamble"/>)・
    /// 最後のルールの後 (<see cref="ThumbnailRuleFile.Trailer"/>) に分けて持ち、<see cref="WriteData"/> で元の位置に書き戻す。</summary>
    public static ThumbnailRuleFile ReadFile(string csv)
    {
        var list = new List<ThumbnailRuleData>();
        var order = Columns;
        string? preamble = null;
        var first = true;
        var i = 0;
        var (records, trailer) = ParseCsv(csv);
        foreach (var (rec, leading) in records)
        {
            if (first)
            {
                first = false;
                // 見出し行 (enabled,name,… の名前が並ぶ行) ならその並びを使う。見出しが無ければ既定の並びで読む
                var head = rec.Select(c => c.Trim().ToLowerInvariant()).ToArray();
                if (head.Contains("name") && head.Contains("match")) { order = head; preamble = leading; continue; }
            }
            i++;
            string Col(string name)
            {
                var ix = Array.IndexOf(order, name);
                return ix >= 0 && ix < rec.Count ? rec[ix].Trim() : "";
            }
            var enabled = Col("enabled").ToLowerInvariant();
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in SplitList(Col("headers")))
            {
                var colon = h.IndexOf(':');
                if (colon > 0) headers[h.Substring(0, colon).Trim()] = h.Substring(colon + 1).Trim();
            }
            var name = Col("name");
            list.Add(new ThumbnailRuleData
            {
                Name     = name.Length > 0 ? name : $"#{i}",
                Enabled  = enabled is not ("0" or "false" or "no" or "off"),
                Match    = Col("match"),
                Result   = Col("result") is { Length: > 0 } r ? r : null,
                FetchUrl = Col("fetch_url") is { Length: > 0 } f ? f : null,
                Headers  = headers,
                Format   = Col("format") is { Length: > 0 } fm ? fm : "json",
                Extract  = SplitList(Col("extract")).ToList(),
                Empty    = Col("empty") is { Length: > 0 } em ? em : "unavailable",
                Display  = Col("display") is { Length: > 0 } dp ? dp : "auto",
                Comment  = leading,
            });
        }
        return new ThumbnailRuleFile(list, preamble, trailer);
    }

    /// <summary>新しく作るファイルの先頭に入れる説明 (利用者が書き換えたらそれを保つ)。</summary>
    private const string DefaultPreamble =
        "# サムネイルルール (利用者のルール)。1 行が 1 つのルール。設定 → サムネイルルール で編集できる。\n" +
        "# headers と extract に複数の値を書くときは || で区切る。カンマや \" を含む項目は \" で囲み、中の \" は \"\" と書く。\n" +
        "# # で始まる行はコメント (設定画面で編集しても残る。ルールの直前のコメントはそのルールと一緒に動き、ルールを消すと一緒に消える)。\n";

    /// <summary>ルールの一覧をファイルの書式 (CSV。見出し行 + 1 行 1 ルール) にする (設定画面からの保存用)。
    /// コメント行は読んだときの位置に書き戻す: <paramref name="preamble"/> = 見出し行の前 (null なら既定の説明)、
    /// 各ルールの <see cref="ThumbnailRuleData.Comment"/> = そのルールの直前、<paramref name="trailer"/> = 最後のルールの後。</summary>
    public static string WriteData(IEnumerable<ThumbnailRuleData> rules, string? preamble = null, string trailer = "")
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(CommentBlock(preamble ?? DefaultPreamble));
        sb.Append(string.Join(",", Columns)).Append('\n');
        foreach (var d in rules)
        {
            sb.Append(CommentBlock(d.Comment));
            var cells = new[]
            {
                d.Enabled ? "1" : "0",
                d.Name,
                d.Match,
                d.Result ?? "",
                d.FetchUrl ?? "",
                string.Join(" " + ListSeparator + " ", d.Headers.Select(h => h.Key + ": " + h.Value)),
                d.Format,
                string.Join(" " + ListSeparator + " ", d.Extract),
                d.Empty,
                d.Display,
            };
            sb.Append(string.Join(",", cells.Select(CsvCell))).Append('\n');
        }
        sb.Append(CommentBlock(trailer));
        return sb.ToString();
    }

    /// <summary>コメント行のかたまりを、行末を揃えて返す (空なら空)。</summary>
    private static string CommentBlock(string? block)
    {
        if (string.IsNullOrEmpty(block)) return "";
        var t = block.Replace("\r", "");
        return t.EndsWith('\n') ? t : t + "\n";
    }

    private static IEnumerable<string> SplitList(string cell)
        => cell.Split(ListSeparator).Select(x => x.Trim()).Where(x => x.Length > 0);

    /// <summary>CSV の 1 項目。カンマ・" ・改行・前後の空白・先頭の # を含むものは " で囲む (1 行 1 ルールを保つため、改行は空白にする)。</summary>
    private static string CsvCell(string? value)
    {
        var v = (value ?? "").Replace("\r", "").Replace('\n', ' ');
        var quote = v.Contains(',') || v.Contains('"') || v.StartsWith('#') || v.StartsWith(' ') || v.EndsWith(' ');
        return quote ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    /// <summary>CSV を行ごとの項目の並びにする (" で囲んだ項目の中のカンマ・"" に対応)。# で始まる行と空行はルールの行ではなく、
    /// 直後の行の <c>Leading</c> (その行の前のコメント。書いてあるとおり) に入れて返す。最後の行より後のものは <c>Trailer</c>。</summary>
    internal static (List<(List<string> Cells, string Leading)> Records, string Trailer) ParseCsv(string text)
    {
        var records = new List<(List<string>, string)>();
        var leading = new System.Text.StringBuilder();
        var pos = 0;
        if (text.Length > 0 && text[0] == '\uFEFF') pos = 1;   // BOM
        while (pos < text.Length)
        {
            // 行の先頭: # の行と空行はコメントとして溜める
            var lineEnd = text.IndexOf('\n', pos);
            var line = lineEnd < 0 ? text.Substring(pos) : text.Substring(pos, lineEnd - pos);
            if (line.TrimStart().StartsWith('#') || line.Trim().Length == 0)
            {
                leading.Append(line.TrimEnd('\r')).Append('\n');
                pos = lineEnd < 0 ? text.Length : lineEnd + 1;
                continue;
            }
            var rec = new List<string>();
            var cell = new System.Text.StringBuilder();
            var quoted = false;      // いま " の中か
            while (pos < text.Length)
            {
                var c = text[pos];
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (pos + 1 < text.Length && text[pos + 1] == '"') { cell.Append('"'); pos += 2; continue; }
                        quoted = false; pos++; continue;
                    }
                    cell.Append(c); pos++; continue;
                }
                if (c == '"' && cell.ToString().Trim().Length == 0) { quoted = true; cell.Clear(); pos++; continue; }
                if (c == ',') { rec.Add(cell.ToString()); cell.Clear(); pos++; continue; }
                if (c == '\n') { pos++; break; }
                if (c == '\r') { pos++; continue; }
                cell.Append(c); pos++;
            }
            rec.Add(cell.ToString());
            records.Add((rec, leading.ToString()));
            leading.Clear();
        }
        return (records, leading.ToString());
    }

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
