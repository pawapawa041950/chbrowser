using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ChBrowser.Models;

namespace ChBrowser.Services.Bbs;

/// <summary>掲示板 1 種類分のアンカー規則をコンパイルしたもの。本文からレス参照 (番号範囲) を抽出する。
/// JS 側 (<c>thread.js</c> の <c>compileAnchorRules</c>) と同じ組み立て方 (規則を優先順に結合し、規則ごとの
/// 名前付きグループ <c>s&lt;番号&gt;</c> でどの規則に当たったかを判別) なので、C# と JS で抽出結果が一致する。</summary>
public sealed class AnchorRuleSet
{
    /// <summary>範囲指定 1 要素 (from..to)。単独番号は from == to。</summary>
    public readonly record struct AnchorRange(long From, long To);

    private sealed record Compiled(AnchorRule Rule, int Index);

    private readonly List<Compiled> _rules = new();
    private readonly Regex          _scan;

    /// <summary>JS へ送る用の規則一覧 (有効なもののみ、元の定義のまま)。</summary>
    public IReadOnlyList<AnchorRule> Rules { get; }

    public AnchorRuleSet(IEnumerable<AnchorRule> rules)
    {
        var alts    = new List<string>();
        var enabled = new List<AnchorRule>();
        foreach (var r in rules)
        {
            if (r is null || !r.Enabled || string.IsNullOrWhiteSpace(r.Pattern)) continue;
            var idx = _rules.Count;
            var src = r.Pattern.Replace("(?<spec>", $"(?<s{idx}>", StringComparison.Ordinal);
            if (ReferenceEquals(src, r.Pattern) || src == r.Pattern) continue; // spec グループ無し
            try { _ = new Regex(src); }
            catch (ArgumentException) { continue; }             // 不正な正規表現は無視 (JS 側と同じ)
            _rules.Add(new Compiled(r, idx));
            enabled.Add(r);
            alts.Add("(?:" + src + ")");
        }
        Rules = enabled;
        _scan = new Regex(alts.Count > 0 ? string.Join("|", alts) : "(?!)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }

    /// <summary>本文から参照範囲を抽出する (出現順)。HTML タグは事前に空白へ置換する (JS と同じ)。
    /// <paramref name="attachmentResolver"/> は添付ファイル名 → レス番号 (attachment 規則用)。null なら解決不能扱い。</summary>
    public List<AnchorRange> ExtractRanges(string? body, Func<string, long?>? attachmentResolver = null)
    {
        var result = new List<AnchorRange>();
        if (string.IsNullOrEmpty(body) || _rules.Count == 0) return result;
        var stripped = StripTags.Replace(body, " ");
        foreach (Match m in _scan.Matches(stripped))
        {
            if (m.Length == 0) continue;
            foreach (var c in _rules)
            {
                var g = m.Groups["s" + c.Index];
                if (!g.Success) continue;
                if (c.Rule.IsAttachment)
                {
                    var n = attachmentResolver?.Invoke(g.Value.Trim());
                    if (n is > 0) result.Add(new AnchorRange(n.Value, n.Value));
                }
                else if (c.Rule.Ranges)
                {
                    ParseRanges(g.Value, result);
                }
                else if (long.TryParse(g.Value, out var single) && single > 0)
                {
                    result.Add(new AnchorRange(single, single));
                }
                break;
            }
        }
        return result;
    }

    /// <summary>参照されているレス番号を展開して返す (範囲は <paramref name="maxSpan"/> 件で打ち切り、重複なし)。
    /// 連鎖 NG 等、「番号集合」だけ要る呼び出し元向け。</summary>
    public HashSet<long> ExtractNumbers(string? body, int maxSpan = 50, Func<string, long?>? attachmentResolver = null)
    {
        var set = new HashSet<long>();
        foreach (var r in ExtractRanges(body, attachmentResolver))
        {
            var to = r.To - r.From > maxSpan ? r.From + maxSpan : r.To;
            for (var n = r.From; n <= to; n++) set.Add(n);
        }
        return set;
    }

    private static readonly Regex StripTags = new("<[^>]+>", RegexOptions.Compiled);

    /// <summary>"3-5,7、9" のような番号指定を範囲配列に分解する (JS の parseAnchorRanges と同じ規則:
    /// カンマは半角 "," / 全角 "，" / 読点 "、" を許容、各範囲は from&lt;=to に正規化、数字が取れない部分は無視)。</summary>
    private static void ParseRanges(string spec, List<AnchorRange> into)
    {
        foreach (var part in spec.Split(RangeSeparators))
        {
            var p = part.Trim();
            if (p.Length == 0) continue;
            var dash = p.IndexOf('-');
            if (dash < 0)
            {
                if (long.TryParse(p, out var n) && n > 0) into.Add(new AnchorRange(n, n));
                continue;
            }
            if (!long.TryParse(p[..dash].Trim(), out var from) || from <= 0) continue;
            if (!long.TryParse(p[(dash + 1)..].Trim(), out var to) || to <= 0) { into.Add(new AnchorRange(from, from)); continue; }
            if (to < from) (from, to) = (to, from);
            into.Add(new AnchorRange(from, to));
        }
    }
    private static readonly char[] RangeSeparators = { ',', '，', '、' };
}

/// <summary>掲示板ごとの「有効なアンカー規則」を解決する登録簿。
/// 規則は提供者の既定値を、設定 (<see cref="AppConfig.AnchorRules"/>) で上書きできる。
/// <see cref="Configure"/> は設定適用時 (<c>MainViewModel.ApplyConfig</c>) に呼ぶ。</summary>
public static class AnchorRuleRegistry
{
    private static IReadOnlyDictionary<string, AnchorRule[]>? _overrides;
    private static readonly Dictionary<string, AnchorRuleSet> _cache = new(StringComparer.Ordinal);
    private static readonly object _lock = new();

    public static void Configure(IReadOnlyDictionary<string, AnchorRule[]>? overrides)
    {
        lock (_lock)
        {
            _overrides = overrides;
            _cache.Clear();
        }
    }

    /// <summary>提供者の有効規則 (設定で上書きされていればそれ、無ければ提供者の既定)。</summary>
    public static AnchorRuleSet For(IBbsProvider provider)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(provider.Id, out var cached)) return cached;
            var rules = _overrides is not null && _overrides.TryGetValue(provider.Id, out var ov) && ov is { Length: > 0 }
                ? ov
                : provider.DefaultAnchorRules.ToArray();
            var set = new AnchorRuleSet(rules);
            _cache[provider.Id] = set;
            return set;
        }
    }

    /// <summary>ホスト名から提供者を解決して規則を返す (未知ホストは 5ch の規則)。</summary>
    public static AnchorRuleSet ForHost(string? host) => For(BbsRegistry.ResolveOrDefault(host));
}
