using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Models;

namespace ChBrowser.Services.Llm;

/// <summary>LLM 接続テストの結果。<see cref="Ok"/> が成否、<see cref="Message"/> が UI に出す詳細。</summary>
public sealed record LlmTestResult(bool Ok, string Message);

/// <summary>LLM が要求してきた 1 件のツール呼び出し。
/// <see cref="ArgumentsJson"/> は LLM が組み立てた JSON 文字列そのまま (= JSON とは限らない、壊れている可能性もある)。</summary>
public sealed record LlmToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>Chat Completions の 1 メッセージ。
/// <see cref="Role"/> は "system" / "user" / "assistant" / "tool"。
/// assistant がツール呼び出しを要求した場合は <see cref="ToolCalls"/> が入る (このとき Content は空文字でもよい)。
/// tool ロール (= ツール実行結果) の場合は <see cref="ToolCallId"/> に対応する call_id を入れる。</summary>
public sealed record LlmChatMessage(string Role, string Content)
{
    /// <summary>assistant メッセージがツール呼び出しを伴う場合の呼び出し列 (= OpenAI 仕様の tool_calls)。</summary>
    public IReadOnlyList<LlmToolCall>? ToolCalls { get; init; }
    /// <summary>tool ロールのメッセージで、対応する assistant.tool_calls[].id を指す。</summary>
    public string? ToolCallId { get; init; }
}

/// <summary>Chat Completions (ストリーミング) の最終結果。
/// 成功で <see cref="ToolCalls"/> が空なら <see cref="Content"/> が応答本文、
/// <see cref="ToolCalls"/> に何か入っていれば「次はこのツールを呼んで結果を返してくれ」の意味。
/// 失敗なら <see cref="Error"/> に理由。</summary>
public sealed record LlmChatResult(
    bool Ok,
    string Content,
    IReadOnlyList<LlmToolCall> ToolCalls,
    string? Error);

/// <summary>
/// OpenAI 互換 Chat Completions API のクライアント。接続確認 (<see cref="TestConnectionAsync"/>) と
/// ストリーミングチャット (<see cref="ChatStreamAsync"/>) を提供する。
///
/// エンドポイントは「base URL (例: https://api.openai.com/v1)」でも
/// 「/chat/completions まで含む完全 URL」でも受け付ける (= <see cref="BuildChatCompletionsUrl"/> で正規化)。
///
/// タイムアウトは <see cref="HttpClient.Timeout"/> ではなくメソッドごとの <see cref="CancellationTokenSource"/>
/// で制御する (= ストリーミングは応答全体が長くなり得るため、HttpClient.Timeout だと長文生成が途中で切れる)。
///
/// <para><b>Function calling (= Agent モード)</b>: <see cref="ChatStreamAsync"/> に <c>tools</c> を渡すと、
/// LLM はテキストの代わりにツール呼び出しを返してくる場合がある。返ってきた <see cref="LlmChatResult.ToolCalls"/>
/// を呼び出し側でローカル実行し、結果を <c>role:"tool"</c> メッセージとして履歴に追加、もう一度
/// <see cref="ChatStreamAsync"/> を呼ぶ — を「ツール呼び出しが返らなくなる」まで繰り返すと
/// Agent ループになる。SSE ストリームは tool_calls も <c>index</c> 単位の delta で送られてくるので、
/// 本クライアントは <see cref="AccumulateToolCallDelta"/> で index ごとに id / name / arguments を蓄積する。</para>
/// </summary>
public sealed class LlmClient : IDisposable
{
    /// <summary>接続確認のタイムアウト。</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);
    /// <summary>ストリーミングチャット全体のタイムアウト (= 長文生成も許容できる長さ)。</summary>
    private static readonly TimeSpan ChatTimeout = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;

    /// <summary>設定すると、各 <see cref="ChatStreamAsync"/> 完了時に当該呼び出しの計測 (推論時間 / 生成時間 /
    /// 出力トークン数) をここへ加算する (= 1 ターン分を Strategist + 全 Worker で合算)。null なら計測しない。</summary>
    public AgentTurnMetrics? ActiveMetrics { get; set; }

    public LlmClient()
    {
        // LLM API は 5ch 通信とは無関係なので MonazillaClient とは別の HttpClient を持つ。
        // Timeout は無限にし、各メソッドが CancellationTokenSource.CancelAfter で個別に制限する
        // (= ストリーミング応答が HttpClient.Timeout で途中切断されるのを防ぐ)。
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>指定設定で OpenAI 互換 API に最小リクエスト (max_tokens=1 の "ping") を投げ、接続可否を返す。
    /// /chat/completions を実際に叩くので、URL の到達性・API キーの有効性・モデル名の正しさを
    /// まとめて検証できる (トークン消費はごく僅か)。</summary>
    public async Task<LlmTestResult> TestConnectionAsync(LlmSettings settings, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiUrl))
            return new LlmTestResult(false, "API URL が未設定です");
        if (string.IsNullOrWhiteSpace(settings.Model))
            return new LlmTestResult(false, "モデル名が未設定です");

        var endpoint = BuildChatCompletionsUrl(settings.ApiUrl);
        if (endpoint is null)
            return new LlmTestResult(false, $"API URL の形式が不正です: {settings.ApiUrl}");

        var payload = new
        {
            model      = settings.Model.Trim(),
            messages   = new[] { new { role = "user", content = "ping" } },
            max_tokens = 1,
        };
        var json = JsonSerializer.Serialize(payload);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TestTimeout);
        try
        {
            using var req = BuildRequest(endpoint, json, settings.ApiKey);
            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

            if (resp.IsSuccessStatusCode)
                return new LlmTestResult(true, $"接続成功 (HTTP {(int)resp.StatusCode})");

            // エラー時は OpenAI 互換のエラー JSON から message を拾えれば優先表示。
            var detail = ExtractErrorMessage(body) ?? Truncate(body, 200);
            return new LlmTestResult(false, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {detail}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new LlmTestResult(false, "タイムアウトしました");
        }
        catch (Exception ex)
        {
            return new LlmTestResult(false, $"接続エラー: {ex.Message}");
        }
    }

    /// <summary>OpenAI 互換 Chat Completions に system + 会話履歴を投げ、応答を SSE ストリーミングで受け取る。
    /// 受信したテキスト断片は到着のたびに <paramref name="onDelta"/> に渡される。
    /// 戻り値の <see cref="LlmChatResult.Content"/> には応答全文 (= 全 delta の連結) が、
    /// <see cref="LlmChatResult.ToolCalls"/> には LLM が要求したツール呼び出し列が入る (= 空ならテキスト応答完了)。
    ///
    /// <paramref name="tools"/> を渡せばその一覧を LLM に提示する (= OpenAI 仕様の <c>tools</c> パラメータ)。
    /// 各要素は <c>{ type:"function", function:{ name, description, parameters:{JSON Schema} } }</c> の形を期待する。
    /// null / 空配列なら通常のテキスト応答モード。
    ///
    /// <para><b>スレッド注意</b>: <paramref name="onDelta"/> は本メソッドの await 継続と同じ同期コンテキストで
    /// 呼ばれる (= 内部は <c>ConfigureAwait(true)</c>)。UI スレッドから呼べば onDelta も UI スレッドで走るので、
    /// 呼び出し側はそのまま WebView2 への post 等ができる。</para></summary>
    public async Task<LlmChatResult> ChatStreamAsync(
        LlmSettings settings,
        IReadOnlyList<LlmChatMessage> messages,
        Action<string> onDelta,
        IReadOnlyList<object>? tools = null,
        CancellationToken ct = default,
        IReadOnlyDictionary<string, object?>? extraBody = null)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiUrl))
            return new LlmChatResult(false, "", Array.Empty<LlmToolCall>(), "API URL が未設定です");
        if (string.IsNullOrWhiteSpace(settings.Model))
            return new LlmChatResult(false, "", Array.Empty<LlmToolCall>(), "モデル名が未設定です");
        if (messages.Count == 0)
            return new LlmChatResult(false, "", Array.Empty<LlmToolCall>(), "送信するメッセージがありません");

        var endpoint = BuildChatCompletionsUrl(settings.ApiUrl);
        if (endpoint is null)
            return new LlmChatResult(false, "", Array.Empty<LlmToolCall>(), $"API URL の形式が不正です: {settings.ApiUrl}");

        // 変則的なメッセージ shape (assistant+tool_calls / tool) も含めて JSON 化するため
        // 各メッセージを個別に object へ落としてから配列にする。
        var msgObjs = messages.Select(SerializeMessage).ToArray();

        // max_tokens を明示送信する (= サーバの default 値が低くて think 中に切れるのを防ぐ)。
        // Context Size の 1/4 を上限とし [2048, 16384] にクランプ。これで多くのリーズニングモデルが
        // think + 本文を出し切れる。下記の値より大きく欲しい場合は ContextSize を増やせばよい。
        var maxTokens = ComputeMaxTokens(settings.ContextSize);

        // tools が指定されていれば payload に tools を追加。空なら通常モード。
        // stream_options.include_usage: 対応サーバ (OpenAI / llama.cpp 等) は最終チャンクで usage を返す
        // → 出力トークン数を正確に取れる (= token/s・総トークン表示用)。非対応サーバは無視するだけ。
        // payload は Dictionary で組む (= 呼び出し側が extraBody で任意フィールドを足せるように)。
        var payload = new Dictionary<string, object?>
        {
            ["model"]          = settings.Model.Trim(),
            ["messages"]       = msgObjs,
            ["stream"]         = true,
            ["stream_options"] = new { include_usage = true },
            ["max_tokens"]     = maxTokens,
        };
        if (tools is { Count: > 0 })
            payload["tools"] = tools.ToArray();
        // extraBody: リーズニング無効化フラグ等を呼び出し側 (NG 判定) が注入する。
        // 同名キーは上書き。OpenAI 互換サーバ (llama.cpp / vLLM) は未知フィールドを基本的に無視する。
        if (extraBody is not null)
            foreach (var kv in extraBody) payload[kv.Key] = kv.Value;
        var json = JsonSerializer.Serialize(payload);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ChatTimeout);
        var sw = System.Diagnostics.Stopwatch.StartNew();   // 計測: 生成時間 / 推論時間 (AgentTurnMetrics 用)
        try
        {
            using var req = BuildRequest(endpoint, json, settings.ApiKey);
            // ResponseHeadersRead: 本文を待たずにヘッダ到着で戻る (= ストリーミング読み出しの前提)。
            using var resp = await _http
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(true);

            if (!resp.IsSuccessStatusCode)
            {
                var body   = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(true);
                var detail = ExtractErrorMessage(body) ?? Truncate(body, 300);
                return new LlmChatResult(
                    false, "", Array.Empty<LlmToolCall>(),
                    $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {detail}");
            }

            using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(true);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var full      = new StringBuilder();
            // tool_calls は SSE で index 単位の delta として送られてくる (= 1 つの呼び出しを複数 chunk で構築)
            // ので、index → 蓄積バッファ の辞書に貯めていく。最後に index 昇順で並べて返す。
            var toolAccum = new SortedDictionary<int, ToolCallAccumulator>();

            // 推論モデル (DeepSeek-R1 等) は delta.reasoning_content という別フィールドで思考過程を送ってくる。
            // それを <think>...</think> マーカーで content ストリームに織り込み、後段 (Markdown renderer +
            // ai-chat.html の think-block CSS) で「思考過程」ブロックとして可視化する。
            // - reasoning と content は通常「reasoning が先に全部 → content が後」だが、念のため
            //   両者の切り替わりを検出して open / close を発火する。
            // - 最後まで content が来ず reasoning だけで終わるケースは、ループ後に閉じタグを補う。
            // - 寝かす場所は full にも入れて履歴に保存する (= 次ターンに「assistant がこう考えてこう答えた」
            //   という流れを残す。OpenAI 系では content に <think> が含まれていても 400 にはならない)。
            const string ThinkOpen  = "<think>";
            const string ThinkClose = "</think>";
            var inReasoning = false;

            // 計測: 推論 (reasoning_content) の開始/終了と、usage 由来の出力トークン数。
            long reasoningStartMs = -1, reasoningEndMs = -1;
            var  completionTokens = -1;

            string? line;
            // SSE: "data: {json}" 行が delta、"data: [DONE]" で終端。空行・コメント行は読み飛ばす。
            while ((line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(true)) is not null)
            {
                if (line.Length == 0) continue;
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line.Substring(5).Trim();
                if (data.Length == 0) continue;
                if (data == "[DONE]") break;

                // usage は最終チャンク (choices 空 + usage) で来ることが多いので、chunk ごとに拾う。
                if (TryParseCompletionTokens(data, out var usageCt)) completionTokens = usageCt;

                if (!TryParseStreamChunk(data, out var contentDelta, out var reasoningDelta, out var toolDeltas))
                    continue;

                if (!string.IsNullOrEmpty(reasoningDelta))
                {
                    if (!inReasoning)
                    {
                        inReasoning = true;
                        full.Append(ThinkOpen);
                        onDelta(ThinkOpen);
                    }
                    if (reasoningStartMs < 0) reasoningStartMs = sw.ElapsedMilliseconds;
                    reasoningEndMs = sw.ElapsedMilliseconds;
                    full.Append(reasoningDelta);
                    onDelta(reasoningDelta);
                }
                if (!string.IsNullOrEmpty(contentDelta))
                {
                    if (inReasoning)
                    {
                        inReasoning = false;
                        reasoningEndMs = sw.ElapsedMilliseconds; // 本文開始 = 推論終了
                        full.Append(ThinkClose);
                        onDelta(ThinkClose);
                    }
                    full.Append(contentDelta);
                    onDelta(contentDelta);
                }
                if (toolDeltas is not null)
                {
                    foreach (var td in toolDeltas) AccumulateToolCallDelta(toolAccum, td);
                }
            }
            // 終端時に reasoning が閉じていなければ閉じる (= content が一切来なかったケース)。
            if (inReasoning)
            {
                full.Append(ThinkClose);
                onDelta(ThinkClose);
            }

            var toolCalls = toolAccum.Values
                .Where(a => !string.IsNullOrEmpty(a.Name))   // name 未受信のものは捨てる (= 不完全 chunk)
                .Select(a => new LlmToolCall(a.Id ?? "", a.Name!, a.Arguments.ToString()))
                .ToArray();

            var content = full.ToString();

            // 計測をターン集計へ加算 (Strategist + 各 Worker の合算)。
            var genMs       = (int)sw.ElapsedMilliseconds;
            var reasoningMs = reasoningStartMs >= 0 ? (int)System.Math.Max(0, reasoningEndMs - reasoningStartMs) : 0;
            var tokens      = completionTokens >= 0 ? completionTokens : EstimateTokens(content);
            ActiveMetrics?.Add(reasoningMs, genMs, tokens);

            // 一部のモデル / サーバ (例: Gemma を llama.cpp で tool 提示) は、ツール呼び出しを OpenAI の
            // tool_calls ではなく本文に <tool_call>{json}</tool_call> のテキストとして吐くことがある。
            // その場合ここでサルベージして本物の tool_calls に変換し、本文からトークンを除去する
            // (= 未実行のまま「成功」と誤判定されるのを防ぐ)。
            if (toolCalls.Length == 0 && content.IndexOf("tool_call", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var (cleaned, salvaged) = SalvageEmbeddedToolCalls(content);
                if (salvaged.Count > 0)
                {
                    ChBrowser.Services.Logging.LogService.Instance.Write(
                        $"[LlmClient] 本文埋め込みのツール呼び出しを {salvaged.Count} 件サルベージ: {string.Join(", ", salvaged.ConvertAll(c => c.Name))}");
                    return new LlmChatResult(true, cleaned, salvaged, null);
                }
                // 抽出できなかった = 形式不明。次回の正確な対応のため生データをログに残す。
                ChBrowser.Services.Logging.LogService.Instance.Write(
                    "[LlmClient] tool_call らしきテキストを検出したが JSON 抽出に失敗。raw content: " + Truncate(content, 1200));
            }

            return new LlmChatResult(true, content, toolCalls, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new LlmChatResult(false, "", Array.Empty<LlmToolCall>(), "タイムアウトしました");
        }
        catch (Exception ex)
        {
            return new LlmChatResult(false, "", Array.Empty<LlmToolCall>(), $"通信エラー: {ex.Message}");
        }
    }

    // ---- 本文埋め込みツール呼び出しのサルベージ (tool_calls 未パース対策) ----

    /// <summary><c>&lt;tool_call ...&gt; {json} &lt;/tool_call&gt;</c> ブロック (Hermes / llama.cpp 系の汎用形式)。
    /// 開きタグの余分な属性や末尾 <c>|</c> (<c>&lt;tool_call|&gt;</c>) も許容する。閉じが無い末尾は別途救済。</summary>
    private static readonly Regex ToolCallBlockRe = new(
        @"<tool_call\b[^>]*>\s*([\s\S]*?)\s*</tool_call\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    /// <summary>閉じタグの無い末尾の <c>&lt;tool_call&gt; {json}</c> (ストリーミング途中で切れたケース)。</summary>
    private static readonly Regex ToolCallOpenTailRe = new(
        @"<tool_call\b[^>]*>\s*(\{[\s\S]*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    /// <summary>残骸の単独 <c>&lt;tool_call&gt;</c> / <c>&lt;/tool_call&gt;</c> トークン (表示に漏らさないため除去)。</summary>
    private static readonly Regex ToolCallStrayRe = new(
        @"</?tool_call\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>本文に埋め込まれたツール呼び出し JSON を抽出し、(トークン除去後の本文, 呼び出し列) を返す。
    /// JSON は <c>{ "name": "...", "arguments"|"parameters": {...} }</c> を想定。</summary>
    private static (string Cleaned, List<LlmToolCall> Calls) SalvageEmbeddedToolCalls(string content)
    {
        var calls = new List<LlmToolCall>();
        foreach (Match m in ToolCallBlockRe.Matches(content))
            if (TryParseToolJson(m.Groups[1].Value, out var name, out var argsJson))
                calls.Add(new LlmToolCall(NewCallId(), name, argsJson));

        // 閉じタグ無しの末尾だけのケース (ブロックが 1 件も取れなかったときに限り試す)。
        if (calls.Count == 0)
        {
            var tail = ToolCallOpenTailRe.Match(content);
            if (tail.Success && TryParseToolJson(tail.Groups[1].Value, out var name, out var argsJson))
                calls.Add(new LlmToolCall(NewCallId(), name, argsJson));
        }

        var cleaned = ToolCallBlockRe.Replace(content, "");
        cleaned = ToolCallStrayRe.Replace(cleaned, "");
        return (cleaned.Trim(), calls);
    }

    /// <summary><c>{ "name":..., "arguments"|"parameters"|"args":... }</c> から name と引数 JSON を取り出す。
    /// 引数が文字列ならそのまま、オブジェクトなら raw JSON 文字列にする。name 欠落 / 不正 JSON は false。</summary>
    private static bool TryParseToolJson(string json, out string name, out string argsJson)
    {
        name = ""; argsJson = "{}";
        var trimmed = (json ?? "").Trim();
        if (trimmed.Length == 0 || trimmed[0] != '{') return false;
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) return false;
            name = n.GetString() ?? "";
            if (name.Length == 0) return false;
            foreach (var key in new[] { "arguments", "parameters", "args" })
            {
                if (root.TryGetProperty(key, out var a))
                {
                    argsJson = a.ValueKind == JsonValueKind.String ? (a.GetString() ?? "{}") : a.GetRawText();
                    break;
                }
            }
            return true;
        }
        catch { return false; }
    }

    private static string NewCallId() => "call_" + Guid.NewGuid().ToString("N").Substring(0, 8);

    /// <summary>1 つの tool_call を index 単位で組み立てるためのバッファ。
    /// id / name は最初の chunk にしか入らず、arguments は chunk ごとに文字列として連結される。</summary>
    private sealed class ToolCallAccumulator
    {
        public string?       Id        { get; set; }
        public string?       Name      { get; set; }
        public StringBuilder Arguments { get; } = new();
    }

    /// <summary>tool_call delta 1 件を index ごとのバッファに反映する。
    /// id / name は到着した最初の値を採用 (= 同じ index で再受信は通常無いが、念のため上書きしない)。
    /// arguments は連結する。</summary>
    private static void AccumulateToolCallDelta(
        SortedDictionary<int, ToolCallAccumulator> acc,
        (int Index, string? Id, string? Name, string? Arguments) td)
    {
        if (!acc.TryGetValue(td.Index, out var a))
        {
            a = new ToolCallAccumulator();
            acc[td.Index] = a;
        }
        if (!string.IsNullOrEmpty(td.Id)   && string.IsNullOrEmpty(a.Id))   a.Id   = td.Id;
        if (!string.IsNullOrEmpty(td.Name) && string.IsNullOrEmpty(a.Name)) a.Name = td.Name;
        if (!string.IsNullOrEmpty(td.Arguments)) a.Arguments.Append(td.Arguments);
    }

    /// <summary>SSE chunk から出力トークン数を取り出す (= <c>usage.completion_tokens</c>、無ければ
    /// llama.cpp の <c>timings.predicted_n</c>)。最終チャンク (choices 空 + usage) で来ることが多い。</summary>
    private static bool TryParseCompletionTokens(string data, out int tokens)
    {
        tokens = 0;
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object
                && u.TryGetProperty("completion_tokens", out var ctEl) && ctEl.ValueKind == JsonValueKind.Number
                && ctEl.TryGetInt32(out var ct) && ct > 0)
            {
                tokens = ct; return true;
            }
            if (root.TryGetProperty("timings", out var t) && t.ValueKind == JsonValueKind.Object
                && t.TryGetProperty("predicted_n", out var pn) && pn.ValueKind == JsonValueKind.Number
                && pn.TryGetInt32(out var pnv) && pnv > 0)
            {
                tokens = pnv; return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>usage が取れないサーバ向けの出力トークン概算 (文字数ベース・粗い)。</summary>
    private static int EstimateTokens(string content)
        => string.IsNullOrEmpty(content) ? 0 : (int)System.Math.Ceiling(content.Length / 3.0);

    /// <summary>SSE の 1 chunk JSON から (content delta, reasoning_content delta, tool_calls delta) を取り出す。
    /// 全部 / どれか / 全部無し (= role だけの先頭 chunk) のどのパターンもありうる。
    /// <c>reasoning_content</c> は DeepSeek-R1 系の推論モデルが思考過程を流すフィールドで、対応していない API では常に null。
    /// 形が壊れていれば false を返して呼び出し側で skip。</summary>
    private static bool TryParseStreamChunk(
        string data,
        out string? contentDelta,
        out string? reasoningDelta,
        out List<(int Index, string? Id, string? Name, string? Arguments)>? toolDeltas)
    {
        contentDelta   = null;
        reasoningDelta = null;
        toolDeltas     = null;
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (!doc.RootElement.TryGetProperty("choices", out var choices)) return false;
            if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return false;
            var first = choices[0];
            if (!first.TryGetProperty("delta", out var delta)) return true; // delta なし (= role chunk 等) は OK

            if (delta.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                contentDelta = content.GetString();
            }

            // DeepSeek-R1 / OpenRouter の reasoning モデルが流す思考過程フィールド。
            // 同じ chunk で content と一緒に来ることはほぼ無いが、来ても問題ないように両方拾う。
            if (delta.TryGetProperty("reasoning_content", out var reasoning) &&
                reasoning.ValueKind == JsonValueKind.String)
            {
                reasoningDelta = reasoning.GetString();
            }

            if (delta.TryGetProperty("tool_calls", out var tcs) &&
                tcs.ValueKind == JsonValueKind.Array)
            {
                toolDeltas = new List<(int, string?, string?, string?)>();
                foreach (var tc in tcs.EnumerateArray())
                {
                    int idx = 0;
                    if (tc.TryGetProperty("index", out var idxEl) && idxEl.ValueKind == JsonValueKind.Number)
                        idx = idxEl.GetInt32();

                    string? id = tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                        ? idEl.GetString() : null;

                    string? name = null, args = null;
                    if (tc.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                    {
                        if (fn.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String)
                            name = nEl.GetString();
                        if (fn.TryGetProperty("arguments", out var aEl) && aEl.ValueKind == JsonValueKind.String)
                            args = aEl.GetString();
                    }
                    toolDeltas.Add((idx, id, name, args));
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>1 メッセージをサーバへ送る形 (= OpenAI 仕様) に整形する。
    /// 通常: <c>{role, content}</c>。
    /// assistant + tool_calls: <c>{role:"assistant", content, tool_calls:[{id,type,function:{name,arguments}}]}</c>。
    /// tool: <c>{role:"tool", content, tool_call_id}</c>。</summary>
    private static object SerializeMessage(LlmChatMessage m)
    {
        if (m.Role == "assistant" && m.ToolCalls is { Count: > 0 })
        {
            return new
            {
                role       = "assistant",
                content    = m.Content,
                tool_calls = m.ToolCalls.Select(t => new
                {
                    id       = t.Id,
                    type     = "function",
                    function = new { name = t.Name, arguments = t.ArgumentsJson },
                }).ToArray(),
            };
        }
        if (m.Role == "tool")
        {
            return new
            {
                role         = "tool",
                content      = m.Content,
                tool_call_id = m.ToolCallId ?? "",
            };
        }
        return new { role = m.Role, content = m.Content };
    }

    /// <summary>POST リクエストを組み立てる (JSON ボディ + 任意の Bearer 認証ヘッダ)。
    /// API キーが空ならヘッダ無しで送る (= 認証不要なローカル LLM サーバ等に対応)。</summary>
    private static HttpRequestMessage BuildRequest(string endpoint, string json, string apiKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        return req;
    }

    /// <summary>設定された URL を /chat/completions エンドポイントに正規化する。
    /// 既に /chat/completions で終わっていればそのまま、それ以外は base URL とみなして付与する。
    /// http/https 以外のスキーム・パース不能な文字列なら null。</summary>
    private static string? BuildChatCompletionsUrl(string apiUrl)
    {
        var trimmed = apiUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;

        var s = trimmed.TrimEnd('/');
        if (s.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return s;
        return s + "/chat/completions";
    }

    /// <summary>OpenAI 互換のエラー JSON ( { "error": { "message": "..." } } または { "error": "..." } )
    /// から message 文字列を取り出す。JSON でない / 形が違う場合は null。</summary>
    private static string? ExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var msg))
                    return msg.GetString();
                if (err.ValueKind == JsonValueKind.String)
                    return err.GetString();
            }
        }
        catch
        {
            // JSON でない (= HTML エラーページ等) ならそのまま呼び出し元で本文を truncate して出す。
        }
        return null;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max) + "…";

    /// <summary>1 リクエストで出力させる token 数の上限。Context Size の 1/4 を狙い、
    /// [2048, 16384] にクランプする。これで think + 本文がだいたい収まる枠を確保する。
    /// 設定が 0 / 未設定なら 4096 をデフォルトに。</summary>
    private static int ComputeMaxTokens(int contextSize)
    {
        if (contextSize <= 0) return 4096;
        var v = contextSize / 4;
        if (v < 2048)  v = 2048;
        if (v > 16384) v = 16384;
        return v;
    }

    public void Dispose() => _http.Dispose();
}
