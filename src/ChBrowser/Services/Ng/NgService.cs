using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using ChBrowser.Models;
using ChBrowser.Services.Storage;

namespace ChBrowser.Services.Ng;

/// <summary>
/// NG ルール管理 + 各レスへのマッチ判定 (Phase 13、Phase 13e でスコープ統合)。
///
/// <para>役割:
/// <list type="bullet">
/// <item><description>NgStorage 経由でルール集合 (グローバル + 板単位) を 1 つのリストとして保持</description></item>
/// <item><description>1 レス × 1 ルール のマッチ判定 (Target に応じて subject 抽出 → MatchKind に応じて部分一致 or 正規表現)</description></item>
/// <item><description>あるスレ全体に対する hidden 集合の計算 (連鎖あぼーんの fixpoint loop 含む)</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class NgService
{
    private readonly NgStorage _storage;
    private NgRuleSet _all = new();

    /// <summary>ワッチョイの 8 桁部分 (xxxx-yyyy)。Post.Name から抽出する。</summary>
    private static readonly Regex WatchoiRegex = new(@"[A-Za-z0-9]{4}-[A-Za-z0-9]{4}", RegexOptions.Compiled);

    public NgService(NgStorage storage)
    {
        _storage = storage;
        _all     = storage.LoadAndMigrate();
    }

    /// <summary>現在保持している全ルール。NgWindow が読んで編集に使う。</summary>
    public NgRuleSet All => _all;

    /// <summary>新しいルール集合で上書き保存。NgWindow の保存ボタンから呼ばれる。</summary>
    public void Save(NgRuleSet set)
    {
        _all = set;
        _storage.Save(set);
    }

    /// <summary>外部編集を反映するため明示的に再ロード。</summary>
    public void Reload()
    {
        _all = _storage.LoadAndMigrate();
    }

    // ---- 判定 ----

    /// <summary>あるスレに対して、NG で hidden になるレス番号集合を計算する (連鎖あぼーん含む)。
    /// 互換 API: 内訳が要らない呼出側用。内部では <see cref="ComputeHiddenWithBreakdown"/> を呼ぶ。</summary>
    public ISet<int> ComputeHidden(IList<Post> posts, string host, string directoryName)
        => ComputeHiddenWithBreakdown(posts, host, directoryName).HiddenNumbers;

    /// <summary>あるスレに対して、NG で hidden になるレス集合を per-rule の内訳付きで計算する。
    ///
    /// <para>戻り値:</para>
    /// <list type="bullet">
    ///   <item><description><c>ByRuleDirect</c>: 各ルールに「直接」マッチしたレス数 (= MatchSingle が true)。
    ///     1 レスに複数ルールがマッチした場合は最初のルールに 1 件として計上 (= 二重計上を避ける)。</description></item>
    ///   <item><description><c>ChainOnly</c>: 直接マッチはしないが、別 hidden レスにアンカーしているせいで連鎖あぼーんになったレス数。</description></item>
    ///   <item><description><c>HiddenNumbers</c>: 上記 2 種を合わせた最終 hidden レス番号集合 (= 旧 ComputeHidden と同じ結果)。</description></item>
    /// </list></summary>
    public NgHiddenBreakdown ComputeHiddenWithBreakdown(IList<Post> posts, string host, string directoryName)
    {
        var byRule = new Dictionary<Guid, int>();
        var hidden = new HashSet<int>();
        var rules  = GetActiveRules(host, directoryName);
        if (rules.Count == 0)
            return new NgHiddenBreakdown(byRule, ChainOnly: 0, hidden);

        // 1. 直接マッチを per-rule に集計 (= 1 レス は最初のマッチルールに加算)
        foreach (var p in posts)
        {
            foreach (var r in rules)
            {
                if (MatchSingle(r, p))
                {
                    byRule[r.Id] = byRule.TryGetValue(r.Id, out var c) ? c + 1 : 1;
                    hidden.Add(p.Number);
                    break;
                }
            }
        }

        var directCount = hidden.Count;
        if (directCount == 0)
            return new NgHiddenBreakdown(byRule, ChainOnly: 0, hidden);

        // 2. 連鎖あぼーん (無限再帰): hidden レスにアンカーしているレスも hidden
        var anchorMap = new Dictionary<int, int[]>(posts.Count);
        foreach (var p in posts) anchorMap[p.Number] = ExtractAnchors(p.Body);

        bool changed;
        do
        {
            changed = false;
            foreach (var p in posts)
            {
                if (hidden.Contains(p.Number)) continue;
                var anchors = anchorMap[p.Number];
                foreach (var a in anchors)
                {
                    if (hidden.Contains(a))
                    {
                        hidden.Add(p.Number);
                        changed = true;
                        break;
                    }
                }
            }
        } while (changed);

        var chainOnly = hidden.Count - directCount;
        return new NgHiddenBreakdown(byRule, chainOnly, hidden);
    }

    /// <summary>ルール 1 件 × レス 1 件のマッチ判定 (= スコープ判定はしない、純粋にパターンのマッチのみ)。
    /// 期限切れ / 無効化 / スコープ不一致のルールはこのメソッドに渡される前に弾かれる前提。</summary>
    public static bool MatchSingle(NgRule rule, Post post)
    {
        var subject = ExtractSubject(rule.Target, post);
        if (string.IsNullOrEmpty(subject)) return false;

        if (rule.MatchKind == "regex")
        {
            try
            {
                return Regex.IsMatch(subject, rule.Pattern);
            }
            catch (ArgumentException ex)
            {
                Debug.WriteLine($"[NgService] invalid regex '{rule.Pattern}': {ex.Message}");
                return false;
            }
        }
        if (string.IsNullOrEmpty(rule.Pattern)) return false;
        return subject.Contains(rule.Pattern, StringComparison.Ordinal);
    }

    /// <summary>正規表現が valid かどうかチェック (UI バリデーション用)。</summary>
    public static bool IsValidRegex(string pattern, out string? errorMessage)
    {
        try
        {
            _ = new Regex(pattern);
            errorMessage = null;
            return true;
        }
        catch (ArgumentException ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    /// <summary>名前フィールドから 8 桁ワッチョイ部分を抽出。bone 名は無視し
    /// `[A-Za-z0-9]{4}-[A-Za-z0-9]{4}` パターンの最初の出現を返す。見つからなければ空文字。</summary>
    public static string ExtractWatchoi(string name)
    {
        var m = WatchoiRegex.Match(name);
        return m.Success ? m.Value : "";
    }

    // ---- 内部ヘルパ ----

    /// <summary>有効ルール (期限切れ / 無効化を除外) のうち、(host, dir) のスレに適用されるものだけを返す。
    /// スコープ判定:
    /// <list type="bullet">
    /// <item><description>BoardDirectory が空 → グローバル (BoardHost の値に関係なく全スレで適用)</description></item>
    /// <item><description>BoardDirectory が設定されていて BoardHost が空 → 任意の host の同 dir スレで適用 (= UI で自由入力されたケース)</description></item>
    /// <item><description>両方設定 → root domain + dir 一致のスレで適用</description></item>
    /// </list></summary>
    private List<NgRule> GetActiveRules(string host, string directoryName)
    {
        var now    = DateTimeOffset.UtcNow;
        var result = new List<NgRule>();
        var root   = DataPaths.ExtractRootDomain(host);
        foreach (var r in _all.Rules)
        {
            if (!IsActive(r, now)) continue;

            // BoardDirectory 空 = グローバル
            if (string.IsNullOrEmpty(r.BoardDirectory))
            {
                result.Add(r);
                continue;
            }

            // dir が一致しない時点で対象外
            if (!string.Equals(r.BoardDirectory, directoryName, StringComparison.Ordinal))
                continue;

            // BoardHost 空 = 任意 host (= 自由入力ルール)
            if (string.IsNullOrEmpty(r.BoardHost))
            {
                result.Add(r);
                continue;
            }

            // 両方設定 → root domain 一致をチェック
            if (string.Equals(DataPaths.ExtractRootDomain(r.BoardHost), root, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(r);
            }
        }
        return result;
    }

    private static bool IsActive(NgRule r, DateTimeOffset now)
    {
        if (!r.Enabled) return false;
        if (r.ExpiresAt is { } exp && exp < now) return false;
        return true;
    }

    private static bool MatchAny(Post p, List<NgRule> rules)
    {
        foreach (var r in rules)
            if (MatchSingle(r, p)) return true;
        return false;
    }

    private static string ExtractSubject(string target, Post p) => target switch
    {
        "name"    => p.Name,
        "id"      => p.Id,
        "watchoi" => ExtractWatchoi(p.Name),
        "word"    => p.Body,
        _         => "",
    };

    private static int[] ExtractAnchors(string body)
    {
        if (string.IsNullOrEmpty(body)) return Array.Empty<int>();
        var list = new List<int>();
        foreach (Match m in AnchorRegex.Matches(body))
        {
            if (!int.TryParse(m.Groups["from"].Value, out var from)) continue;
            var toStr = m.Groups["to"].Value;
            int to = string.IsNullOrEmpty(toStr) || !int.TryParse(toStr, out var t) ? from : t;
            if (to < from) (from, to) = (to, from);
            if (to - from > 50) to = from + 50;
            for (var n = from; n <= to; n++) list.Add(n);
        }
        return list.ToArray();
    }

    private static readonly Regex AnchorRegex = new(@">>(?<from>\d+)(?:-(?<to>\d+))?", RegexOptions.Compiled);
}

/// <summary>NG hidden 集合の内訳。<see cref="NgService.ComputeHiddenWithBreakdown"/> の戻り値。</summary>
public sealed record NgHiddenBreakdown(
    Dictionary<Guid, int> ByRuleDirect,
    int ChainOnly,
    HashSet<int> HiddenNumbers);
