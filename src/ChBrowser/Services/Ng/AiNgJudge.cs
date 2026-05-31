using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Models;
using ChBrowser.Services.Llm;

namespace ChBrowser.Services.Ng;

/// <summary>1 レスの「攻撃度」を軽量 LLM で 1..5 判定する (第1弾・愚直に 1 件ずつ)。
///
/// <para>5 = 完全に攻撃的 / 4 = どちらかというと攻撃的 / 3 = 判断に迷う / 2 = おそらく穏便 / 1 = 完全に穏便。
/// LLM には「1 文字 (1..5) だけ返せ」と指示し、応答先頭の 1..5 の数字を採点として拾う
/// (parse 失敗時は 3 = 判断に迷う = 既定では非表示にしない)。</para></summary>
public static class AiNgJudge
{
    private const string SystemPrompt =
        "あなたは 5ch のレスがどれだけ攻撃的か (誹謗中傷・煽り・罵倒・差別・喧嘩腰か) を判定する分類器です。\n" +
        "次の 5 段階で採点し、**数字 1 文字だけ**を出力してください (説明や記号は一切不要)。\n" +
        "5 = 完全に攻撃的 / 4 = どちらかというと攻撃的 / 3 = 判断に迷う / 2 = おそらく穏便 / 1 = 完全に穏便";

    private static readonly Regex BrRe  = new(@"(?i)<br\s*/?>", RegexOptions.Compiled);
    private static readonly Regex TagRe = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex DigitRe = new(@"[1-5]", RegexOptions.Compiled);

    /// <summary>リーズニング (思考過程生成) を無効化するためにリクエストボディへ詰め込む設定一式。
    /// サーバ / モデルによって効くキーが違うので「一般的なものを全部」入れている。OpenAI 互換サーバ
    /// (llama.cpp / vLLM) は未知フィールドを基本的に無視するため、効かないキーがあっても害はない。
    ///
    /// <list type="bullet">
    /// <item><c>chat_template_kwargs.enable_thinking=false</c> — Qwen3 / <b>Gemma 4</b> 等のチャット
    /// テンプレート変数経由 (llama.cpp は <c>--jinja</c> 必須。テンプレートが使わない変数は無視される)。</item>
    /// <item>トップレベルの別名 (<c>enable_thinking</c> / <c>reasoning_effort</c> / <c>reasoning_budget</c> /
    /// <c>thinking_budget</c>) — どれか 1 つが効けばよい。</item>
    /// <item><c>reasoning={enabled:false,max_tokens:0}</c> (OpenRouter 系) /
    /// <c>thinking={type:"disabled"}</c> (Anthropic 系) — ゲートウェイ経由のとき用。</item>
    /// </list></summary>
    private static readonly IReadOnlyDictionary<string, object?> ReasoningOff = new Dictionary<string, object?>
    {
        ["chat_template_kwargs"] = new Dictionary<string, object?>
        {
            ["enable_thinking"] = false, // Qwen3 / Gemma 4
            ["thinking"]        = false, // 一部テンプレート
            ["reasoning"]       = false, // 一部テンプレート
        },
        ["enable_thinking"]  = false,
        ["reasoning_effort"] = "none",   // OpenAI 系 (minimal/low/medium/high/none)
        ["reasoning_budget"] = 0,        // llama.cpp
        ["thinking_budget"]  = 0,
        ["reasoning"]        = new Dictionary<string, object?> { ["enabled"] = false, ["max_tokens"] = 0 },
        ["thinking"]         = new Dictionary<string, object?> { ["type"] = "disabled" },
    };

    /// <summary>1 レスを判定して 1..5 を返す。LLM 失敗時は -1 (= 未判定・後で再試行)。
    /// <paramref name="disableReasoning"/>=true のとき、リクエストにリーズニング無効化設定一式
    /// (<see cref="ReasoningOff"/>) を付加する。</summary>
    public static async Task<int> JudgeAsync(LlmClient llm, LlmSettings settings, Post post, bool disableReasoning, CancellationToken ct)
    {
        var body = CleanForJudge(post.Body);
        if (string.IsNullOrWhiteSpace(body)) return 1; // 空レスは穏便扱い

        var userPrompt = "次のレスを採点 (1..5 の数字 1 文字だけ):\n\n" + body;
        var messages = new List<LlmChatMessage>
        {
            new("system", SystemPrompt),
            new("user", userPrompt),
        };

        // (診断) 入力プロンプトをログ出力 (処理が遅い原因の切り分け用)。
        var log = ChBrowser.Services.Logging.LogService.Instance;
        log.Write($"[AiNgJudge] >>> No.{post.Number} model={settings.Model} 入力 ({body.Length}字): {Preview(userPrompt)}");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        LlmChatResult result;
        try
        {
            result = await llm.ChatStreamAsync(settings, messages, _ => { }, null, ct,
                disableReasoning ? ReasoningOff : null).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            sw.Stop();
            log.Write($"[AiNgJudge] <<< No.{post.Number} 例外 {sw.ElapsedMilliseconds}ms: {ex.Message}");
            return -1;
        }
        sw.Stop();

        if (!result.Ok)
        {
            log.Write($"[AiNgJudge] <<< No.{post.Number} 失敗 {sw.ElapsedMilliseconds}ms: {result.Error}");
            return -1;
        }

        var raw = result.Content ?? "";
        var (text, _) = ChatArchive.SplitThink(raw); // reasoning モデルの think を除去
        var m = DigitRe.Match(text ?? "");
        var score = (m.Success && int.TryParse(m.Value, out var s) && s is >= 1 and <= 5) ? s : 3;

        // (診断) 出力 (生応答 + think 除去後 + 採点結果) と所要時間をログ出力。
        log.Write($"[AiNgJudge] <<< No.{post.Number} {sw.ElapsedMilliseconds}ms score={score} 生応答({raw.Length}字): {Preview(raw)}");
        return score; // 解釈不能時は 3 = 判断に迷う (既定しきい値では非表示にしない)
    }

    /// <summary>ログ用にプロンプト / 応答を 1 行・短めに整形する (改行は \\n に潰し、長すぎる場合は打ち切り)。</summary>
    private static string Preview(string s)
    {
        s = s.Replace("\r", "").Replace("\n", "\\n");
        return s.Length > 300 ? s.Substring(0, 300) + "…" : s;
    }

    /// <summary>本文を判定向けに軽く整形する (br→改行、タグ除去、長すぎる本文は打ち切り)。</summary>
    private static string CleanForJudge(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var s = BrRe.Replace(raw, "\n");
        s = TagRe.Replace(s, "");
        s = System.Net.WebUtility.HtmlDecode(s).Trim();
        if (s.Length > 1000) s = s.Substring(0, 1000);
        return s;
    }
}
