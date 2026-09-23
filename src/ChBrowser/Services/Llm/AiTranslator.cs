using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Models;

namespace ChBrowser.Services.Llm;

/// <summary>翻訳するレス 1 件 (<see cref="Text"/> はプレーンテキスト)。</summary>
public sealed record TranslateItem(long Number, string Text);

/// <summary>AI 翻訳 (レス本文を日本語に)。1 回の推論で 1 レスだけ訳す (まとめると取り違え・欠落が起きやすいため)。
///
/// <list type="bullet">
/// <item><description>本文はプレーンテキストにしてから送る (<see cref="ToPlain"/>)。アンカー (<c>&gt;&gt;N</c>) と URL は
///   <c>⟦n⟧</c> に置き換えて送り、訳文で元に戻す (= リンク・アンカー・画像がそのまま効く。<see cref="Mask"/>)。</description></item>
/// <item><description>スレ全体の翻訳では、もともと日本語のレス・訳す文字が無いレスは送らない (<see cref="NeedsTranslation"/>)。</description></item>
/// </list></summary>
public static class AiTranslator
{
    private const string SystemPrompt =
        "あなたは掲示板の書き込みを日本語に翻訳する翻訳者です。\n" +
        "ユーザーが送る 1 件のレス本文を自然な日本語に翻訳し、訳文だけを出力してください (前置き・説明・引用符・コードブロックは不要)。\n" +
        "規則:\n" +
        "- ⟦1⟧ のような記号は、リンクやレスへの参照の目印です。書き換えず、訳文の対応する位置にそのまま残してください。\n" +
        "- 改行の位置はできるだけ保ってください。\n" +
        "- 掲示板らしい口語・スラングは、意味が伝わる自然な日本語の口語にしてください。固有名詞は無理に訳さなくてかまいません。\n" +
        "- すでに日本語の部分はそのまま残してください。\n" +
        "- 内容についての注釈・要約・意見は付けないでください。";

    // ---- 本文 ↔ プレーンテキスト ----

    private static readonly Regex BrRe  = new(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TagRe = new(@"<[^>]+>", RegexOptions.Compiled);

    /// <summary>レス本文 (dat 方言: 改行は \n か &lt;br&gt;、タグ混じり) → プレーンテキスト。</summary>
    public static string ToPlain(string body)
    {
        var s = BrRe.Replace(body ?? "", "\n");
        s = TagRe.Replace(s, "");
        return WebUtility.HtmlDecode(s).Replace("\r\n", "\n").Trim();
    }

    /// <summary>訳文を表示用の本文にする。LLM が出した &lt; はタグとして解釈されないよう全角にする
    /// (本文の描画はタグを解釈するため。reddit の本文と同じ扱い)。</summary>
    public static string ToDisplayBody(string translated)
        => (translated ?? "").Replace("\r\n", "\n").Replace("<", "＜").Trim();

    // ---- 日本語判定 ----

    private static bool IsKana(char c) => c is >= '぀' and <= 'ヿ' or >= 'ｦ' and <= 'ﾟ';
    private static bool IsIdeograph(char c) => c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿';

    /// <summary>日本語の文とみなすか。かなを含めば日本語 (中国語はかなを含まない)。かな無しでも漢字だけの短い書き込み
    /// (「草」「乙」「同意」等) は日本語扱い。</summary>
    public static bool LooksJapanese(string plain)
    {
        var text = Mask(plain).Masked;
        int kana = 0, ideo = 0, latin = 0, letters = 0;
        foreach (var c in text)
        {
            if (!char.IsLetter(c)) continue;
            letters++;
            if (IsKana(c)) kana++;
            else if (IsIdeograph(c)) ideo++;
            else if (c < 0x250) latin++;
        }
        if (kana >= 3 || (kana > 0 && kana * 10 >= letters)) return true;
        return kana == 0 && latin == 0 && ideo > 0 && ideo <= 4;
    }

    /// <summary>スレ全体の翻訳で送るべきレスか: 訳す文字 (アンカー・URL 以外の文字) があり、日本語でない。</summary>
    public static bool NeedsTranslation(string plain)
    {
        var masked = Mask(plain).Masked;
        var letters = masked.Count(char.IsLetter);
        return letters >= 2 && !LooksJapanese(plain);
    }

    // ---- アンカー・URL の保護 ----

    private static readonly Regex ProtectRe = new(
        @"(?:h?ttps?|sssp)://[^\s<>""]+|(?:>>|＞＞|≫)\s*\d+(?:\s*[-,，、]\s*\d+)*|>No\.\d+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TokenRe = new(@"[⟦\[【]{1,2}\s*(\d+)\s*[⟧\]】]{1,2}", RegexOptions.Compiled);

    /// <summary>アンカーと URL を ⟦1⟧, ⟦2⟧ … に置き換える。</summary>
    public static (string Masked, List<string> Tokens) Mask(string plain)
    {
        var tokens = new List<string>();
        var masked = ProtectRe.Replace(plain ?? "", m => { tokens.Add(m.Value); return "⟦" + tokens.Count + "⟧"; });
        return (masked, tokens);
    }

    /// <summary>⟦n⟧ を元に戻す。訳文から消えていた分は末尾に足す (リンク・アンカーを失わない)。</summary>
    public static string Unmask(string text, IReadOnlyList<string> tokens)
    {
        var used = new HashSet<int>();
        var s = TokenRe.Replace(text ?? "", m =>
        {
            var i = int.Parse(m.Groups[1].Value) - 1;
            if (i < 0 || i >= tokens.Count) return m.Value;
            used.Add(i);
            return tokens[i];
        });
        var missing = Enumerable.Range(0, tokens.Count).Where(i => !used.Contains(i)).Select(i => tokens[i]).ToList();
        if (missing.Count > 0) s = s.TrimEnd() + "\n" + string.Join("\n", missing);
        return s;
    }

    // ---- 送信 (1 回の推論で 1 レス) ----

    /// <summary>1 レスを翻訳する (戻り値: 表示用に整形した訳文。応答が空なら null)。
    /// LLM が失敗 (接続エラー等) したら <see cref="AiTranslateException"/>。</summary>
    public static async Task<string?> TranslateOneAsync(
        LlmClient llm, LlmSettings settings, string plain, bool disableReasoning, CancellationToken ct)
    {
        var (masked, tokens) = Mask(plain);
        var messages = new[]
        {
            new LlmChatMessage("system", SystemPrompt),
            new LlmChatMessage("user", masked),
        };
        var chat = await llm.ChatStreamAsync(settings, messages, _ => { }, null, ct,
            disableReasoning ? ChBrowser.Services.Ng.AiNgJudge.ReasoningOff : null).ConfigureAwait(false);
        if (!chat.Ok) throw new AiTranslateException(chat.Error ?? "翻訳の要求に失敗しました");

        var (text, _) = ChatArchive.SplitThink(chat.Content ?? "");
        text = StripWrapping(text);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return ToDisplayBody(Unmask(text, tokens));
    }

    /// <summary>モデルが付けがちな包み (コードブロック ``` / 全体を囲む引用符) を外す。</summary>
    public static string StripWrapping(string text)
    {
        var t = (text ?? "").Trim();
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNl = t.IndexOf('\n');
            t = firstNl >= 0 ? t[(firstNl + 1)..] : "";
            if (t.EndsWith("```", StringComparison.Ordinal)) t = t[..^3];
            t = t.Trim();
        }
        if (t.Length >= 2 && ((t[0] == '"' && t[^1] == '"') || (t[0] == '「' && t[^1] == '」' && t.IndexOf('」') == t.Length - 1)))
            t = t[1..^1].Trim();
        return t;
    }
}

/// <summary>翻訳の要求自体が失敗した (接続エラー・タイムアウト等)。</summary>
public sealed class AiTranslateException(string message) : Exception(message);
