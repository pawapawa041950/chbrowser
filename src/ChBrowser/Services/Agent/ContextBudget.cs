using System.Text.Json;
using ChBrowser.Models;
using ChBrowser.Services.Llm;

namespace ChBrowser.Services.Agent;

/// <summary>エージェントの文脈 (送信するメッセージ列) の大きさの見積もりと切り詰め。Strategist / Worker / ToolRuntime 共通。
///
/// <list type="bullet">
/// <item><description>トークン数はサーバに聞かず文字種で概算する (ASCII 4 文字 ≒ 1 トークン、それ以外 1 文字 ≒ 1 トークン。日本語は多めに見積もる側)。</description></item>
/// <item><description>入力に使える量 = コンテキスト長 − 出力枠 (<see cref="LlmClient.ComputeMaxTokens"/>) − ツール定義 − 余白。</description></item>
/// <item><description>過去の思考過程 (<c>&lt;think&gt;</c>) は履歴に残さない (推論モデルは過去の思考を送り返さない前提のものが多く、文脈も大きく食う)。</description></item>
/// </list></summary>
public static class ContextBudget
{
    /// <summary>コンテキスト長が未設定 (0) のときに仮定する値。</summary>
    public const int DefaultContextSize = 32768;

    public static int ContextSizeOf(LlmSettings s) => s.ContextSize > 0 ? s.ContextSize : DefaultContextSize;

    /// <summary>文字列のトークン数の概算。</summary>
    public static int EstimateTokens(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int ascii = 0, other = 0;
        foreach (var ch in s)
        {
            if (ch < 0x80) ascii++;
            else other++;
        }
        return ascii / 4 + other + 1;
    }

    /// <summary>メッセージ列のトークン数の概算 (本文 + ツール呼び出しの引数 + 1 件ごとの枠)。</summary>
    public static int EstimateTokens(IEnumerable<LlmChatMessage> messages)
    {
        var n = 0;
        foreach (var m in messages)
        {
            n += EstimateTokens(m.Content) + 4;
            if (m.ToolCalls is { } calls)
                foreach (var c in calls) n += EstimateTokens(c.Name) + EstimateTokens(c.ArgumentsJson) + 4;
        }
        return n;
    }

    /// <summary>ツール定義 (毎回送る) のトークン数の概算。</summary>
    public static int EstimateToolDefs(IReadOnlyList<object> toolDefs)
        => toolDefs.Count == 0 ? 0 : EstimateTokens(JsonSerializer.Serialize(toolDefs));

    /// <summary>入力 (メッセージ列) に使ってよいトークン数。下限は 2048 (極端に小さい設定でも動かすため)。</summary>
    public static int InputBudget(LlmSettings s, int toolDefsTokens)
    {
        var ctx = ContextSizeOf(s);
        var budget = ctx - LlmClient.ComputeMaxTokens(s.ContextSize) - toolDefsTokens - ctx / 16;
        return System.Math.Max(2048, budget);
    }

    /// <summary>応答本文から思考過程 (<c>&lt;think&gt;…&lt;/think&gt;</c>) を除く (履歴に残す形)。</summary>
    public static string StripThink(string? content) => ChatArchive.SplitThink(content ?? "").Text;

    /// <summary>文字列を概算 <paramref name="maxTokens"/> トークンまでに切る。切ったら (先頭部分, true)。</summary>
    public static (string Text, bool Truncated) TruncateToTokens(string s, int maxTokens)
    {
        if (EstimateTokens(s) <= maxTokens) return (s, false);
        int ascii = 0, other = 0, i = 0;
        for (; i < s.Length; i++)
        {
            if (s[i] < 0x80) ascii++;
            else other++;
            if (ascii / 4 + other >= maxTokens) break;
        }
        return (s[..i], true);
    }

    /// <summary>ツール 1 回の結果に使ってよいトークン数 (コンテキストの 1/4。1 回の結果で文脈を食い尽くさないため)。</summary>
    public static int ToolOutputBudget(LlmSettings s) => System.Math.Max(1500, ContextSizeOf(s) / 4);

    /// <summary>tool 結果を上限トークン数に収める。切った場合は、全文が archive にあることと取り直し方を末尾に添える。</summary>
    public static string CapToolOutput(string raw, int maxTokens, string? archiveId)
    {
        var (text, truncated) = TruncateToTokens(raw, maxTokens);
        if (!truncated) return raw;
        return text + $"\n…[結果が長すぎるため約 {maxTokens} トークンで切った (全体は約 {EstimateTokens(raw)} トークン)。" +
               (archiveId is null ? "" : $"全文は archive id {archiveId} に保存済み。") +
               "必要な部分は引数 (keyword / categories / limit / start〜end など) を絞って呼び直すこと]";
    }

    /// <summary>メッセージ列が <paramref name="budget"/> トークンを超えていたら、古い tool 結果から順に短い置き換えにする。
    /// <paramref name="protectFrom"/> 以降 (= 直近の往復) と system は触らない。<paramref name="placeholder"/> は (メッセージ位置, 元の本文) → 置き換え文。
    /// 戻り値: 置き換えた件数。</summary>
    public static int CompactToolResults(List<LlmChatMessage> messages, int budget, int protectFrom,
                                         System.Func<int, string, string> placeholder)
    {
        var total = EstimateTokens(messages);
        if (total <= budget) return 0;
        var replaced = 0;
        for (var i = 1; i < messages.Count && i < protectFrom && total > budget; i++)
        {
            var m = messages[i];
            if (m.Role != "tool" || m.Content.StartsWith(CompactedMark, System.StringComparison.Ordinal)) continue;
            var repl = CompactedMark + placeholder(i, m.Content);
            total -= EstimateTokens(m.Content) - EstimateTokens(repl);
            messages[i] = m with { Content = repl };
            replaced++;
        }
        return replaced;
    }

    /// <summary>置き換え済みの印 (二重に置き換えないため)。</summary>
    public const string CompactedMark = "[省略] ";
}
