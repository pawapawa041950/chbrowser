using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ChBrowser.Models;

namespace ChBrowser.Services.Bbs;

/// <summary>「引用文を返信として扱う」の引用先の解決 (C# 側。スレ表示 thread.js の <c>quoteTargetsOf</c> と同じ規則)。
/// 「&gt;引用文」の行を、その文を書いた前のレスへの返信とみなす。自分への返信の検知に使う。純粋関数。
///
/// <list type="number">
/// <item><description>行が丸ごと一致する前のレスがあれば、一番近いもの。</description></item>
/// <item><description>無ければ直前から最大 <see cref="ScanLimit"/> 件さかのぼり、文を含む行を持つレス (自分の文として書いた行を優先、無ければ引用行)。</description></item>
/// </list>
/// <c>&gt;&gt;文</c> (引用の引用) は <c>&gt;</c> を 1 つ外した <c>&gt;文</c> を探す。アンカーだけの行と、<see cref="MinChars"/> 文字未満の引用は対象外。</summary>
public static class QuoteReplyResolver
{
    public const int MinChars  = 2;
    public const int ScanLimit = 300;

    private static readonly Regex TagRe   = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex SpaceRe = new(@"[\s　]+", RegexOptions.Compiled);

    public static string Normalize(string? s) => SpaceRe.Replace(TagRe.Replace(s ?? "", ""), " ").Trim();

    private static bool IsQuoteLine(string t) => t.Length > 0 && (t[0] == '>' || t[0] == '＞');

    private static List<string> LinesOf(Post p)
        => Regex.Split(p.Body ?? "", @"<br\s*/?>|\n", RegexOptions.IgnoreCase).Select(Normalize).Where(l => l.Length > 0).ToList();

    /// <summary><paramref name="targets"/> の各レスについて、引用先のレス番号の集合を返す (解決できたものだけ)。
    /// <paramref name="allPosts"/> はスレの全レス (<paramref name="targets"/> を含む。順不同)。</summary>
    public static Dictionary<long, HashSet<long>> Resolve(IEnumerable<Post> allPosts, IEnumerable<Post> targets, AnchorRuleSet? rules = null)
    {
        var posts = allPosts.GroupBy(p => p.Number).Select(g => g.First()).OrderBy(p => p.Number).ToList();
        var lines = posts.ToDictionary(p => p.Number, LinesOf);
        var index = new Dictionary<string, List<long>>(StringComparer.Ordinal);   // 行 → レス番号 (昇順)
        foreach (var p in posts)
            foreach (var l in lines[p.Number])
            {
                if (!index.TryGetValue(l, out var list)) index[l] = list = new List<long>();
                if (list.Count == 0 || list[^1] != p.Number) list.Add(p.Number);
            }
        var numbers = posts.Select(p => p.Number).ToList();

        var result = new Dictionary<long, HashSet<long>>();
        foreach (var t in targets)
        {
            var set = new HashSet<long>();
            foreach (var line in lines.TryGetValue(t.Number, out var ls) ? ls : LinesOf(t))
            {
                if (!IsQuoteLine(line)) continue;
                if (rules is not null && rules.IsWholeAnchor(line)) continue;   // アンカーだけの行

                var q = line[1..].Trim();
                if (q.Length < MinChars) continue;
                var n = ResolveOne(q, t.Number, index, numbers, lines);
                if (n > 0 && n != t.Number) set.Add(n);
            }
            if (set.Count > 0) result[t.Number] = set;
        }
        return result;
    }

    private static long ResolveOne(string q, long before, Dictionary<string, List<long>> index, List<long> numbers, Dictionary<long, List<string>> lines)
    {
        if (index.TryGetValue(q, out var exact))
            for (var i = exact.Count - 1; i >= 0; i--)
                if (exact[i] < before) return exact[i];

        var idx = numbers.BinarySearch(before);
        idx = idx >= 0 ? idx - 1 : ~idx - 1;
        long quoteHit = 0;
        for (var k = 0; idx >= 0 && k < ScanLimit; idx--, k++)
        {
            foreach (var line in lines[numbers[idx]])
            {
                if (!line.Contains(q, StringComparison.Ordinal)) continue;
                if (!IsQuoteLine(line)) return numbers[idx];
                if (quoteHit == 0) quoteHit = numbers[idx];
            }
        }
        return quoteHit;
    }
}
