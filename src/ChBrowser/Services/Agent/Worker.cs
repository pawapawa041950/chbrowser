using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChBrowser.Models;
using ChBrowser.Services.Llm;

namespace ChBrowser.Services.Agent;

/// <summary>L2 タスク実行ワーカー。doc/ai-agent-design.md §2.2 / §4.5 (D2 / D9 / D10)。
///
/// <para>1 つの <see cref="TaskSpec"/> だけを所有する <b>使い捨て</b> ReAct ループ。タスクごとに新しい
/// インスタンス + 新鮮な会話 (system + タスク + そのタスク内の tool 往復のみ) を作り、完了で破棄する
/// (= 生 tool 出力が Worker のスコープを超えない / D2)。Strategist からは <c>dispatch_task</c> ツール
/// 1 呼び出しとして見える。</para>
///
/// <para>ループの規則:
/// <list type="bullet">
/// <item><description>各ツール結果の末尾に残り回数を付ける (モデルに回数を数えさせない)。</description></item>
/// <item><description>同じツールを同じ引数で呼び直したら実行せず、既に結果がある旨を返す (回数は消費しない)。</description></item>
/// <item><description>履歴には思考過程を残さず、文脈が溢れそうなら古い tool 結果から省略にする (原文は archive id で引ける)。</description></item>
/// <item><description>予算切れ / ラウンド上限 / 停滞で打ち切るときは、ツールを submit_result だけにして 1 回まとめさせる
///   (= 集めた情報を捨てずに partial で返す)。</description></item>
/// </list></para>
///
/// <para>停止条件 (D9): <c>submit_result</c> 呼び出し (done/partial/failed) / 打ち切り → まとめて partial / LLM 失敗 → failed。
/// reasoning モデルが <c>submit_result</c> を呼び忘れてテキストだけ返した場合は「done の finding」とみなす
/// 寛容フォールバック (D10 終端の堅牢性)。</para></summary>
public sealed class Worker
{
    private readonly LlmClient   _llm;
    private readonly LlmSettings _settings;     // Worker 接続 (D7)
    private readonly ToolRuntime _runtime;
    private readonly string      _systemPrompt;

    /// <summary>「ツールも本文も無い」停滞ラウンドの許容連続数。超えたらまとめさせて打ち切る。</summary>
    private const int MaxEmptyRounds = 3;
    /// <summary>直近の tool 結果は省略しない件数 (= 今読んでいる結果を消さない)。</summary>
    private const int KeepRecentToolResults = 2;

    public Worker(LlmClient llm, LlmSettings workerSettings, ToolRuntime runtime, string? systemPrompt = null)
    {
        _llm          = llm;
        _settings     = workerSettings;
        _runtime      = runtime;
        _systemPrompt = string.IsNullOrWhiteSpace(systemPrompt) ? DefaultSystemPrompt : systemPrompt!;
    }

    /// <summary>タスクを 1 つ実行して <see cref="TaskResult"/> を返す。作業ログは <paramref name="section"/> に流す。
    /// 中断 (<paramref name="ct"/>) されたら区画を閉じてから <see cref="OperationCanceledException"/> を投げ直す。</summary>
    public async Task<TaskResult> RunAsync(TaskSpec spec, IWorkSection section, CancellationToken ct)
    {
        try
        {
            return Finish(section, await RunCoreAsync(spec, section, ct).ConfigureAwait(true));
        }
        catch (OperationCanceledException)
        {
            section.Complete(TaskOutcome.Failed, "中断しました");
            throw;
        }
    }

    private async Task<TaskResult> RunCoreAsync(TaskSpec spec, IWorkSection section, CancellationToken ct)
    {
        var messages = new List<LlmChatMessage>
        {
            new("system", _systemPrompt),
            new("user",   BuildTaskPrompt(spec)),
        };
        var toolDefs    = new List<object>(_runtime.GetWorkerToolDefinitions()) { SubmitResultToolDef() };
        var inputBudget = ContextBudget.InputBudget(_settings, ContextBudget.EstimateToolDefs(toolDefs));
        // 1 回の結果の上限: 入力枠の 1/3 (直近 2 件の結果 + タスク等が枠に収まるように)
        var outputCap   = System.Math.Max(800, inputBudget / 3);

        var maxCalls    = System.Math.Max(1, spec.Limits.MaxToolCalls);
        var maxRounds   = maxCalls + 8;   // 停滞の促し / 重複呼び出しに余裕を持たせる
        var toolCalls   = 0;
        var evidenceIds = new List<string>();
        var emptyRounds = 0;
        var archiveOf   = new Dictionary<string, (string Name, string? ArchiveId)>(System.StringComparer.Ordinal); // tool_call id → (ツール名, archive id)
        var done        = new HashSet<string>(System.StringComparer.Ordinal); // 実行済み (ツール名 + 引数)
        string? stopReason = null;
        var stagnated   = false;

        for (var round = 0; round < maxRounds && stopReason is null; round++)
        {
            Compact(messages, archiveOf, inputBudget);
            var result = await _llm.ChatStreamAsync(_settings, messages, section.Stream, toolDefs, ct).ConfigureAwait(true);

            // LLM 失敗 → failed TaskResult (= Strategist が処理する / D15)。
            if (!result.Ok)
                return new TaskResult(spec.Id, TaskOutcome.Failed, $"Worker の LLM 呼び出しに失敗: {result.Error}", evidenceIds, toolCalls);

            var text = ContextBudget.StripThink(result.Content);

            // ツールなしラウンド。
            if (result.ToolCalls.Count == 0)
            {
                // 本文に <tool_call> マーカーが混ざる = ツール呼び出しをテキストで書いてしまい未実行のケース
                // (例: Gemma×llama.cpp が <tool_call|> を本文に吐く)。これを done にすると「表示しました」誤報告になる。
                var toolAttempt = TextToolMarkerRe.IsMatch(text);

                // 本文がある (かつツール書き損じでない) = 最終報告を submit_result の代わりにテキストで返した寛容ケース → done (D10)。
                if (!toolAttempt && !string.IsNullOrWhiteSpace(text))
                    return new TaskResult(spec.Id, TaskOutcome.Done, text.Trim(), evidenceIds, toolCalls);

                // 停滞ラウンド (思考だけ / 空 / ツール書き損じ) は done にしない。継続を促し、続くならまとめさせて打ち切る。
                if (++emptyRounds >= MaxEmptyRounds)
                {
                    stagnated  = true;
                    stopReason = toolAttempt
                        ? "ツール呼び出しがテキストとして出力され、実行されないまま停滞した"
                        : "ツールも本文も無い応答 (思考のみ) が続いた";
                    break;
                }
                messages.Add(new("assistant", text));
                messages.Add(new("user", toolAttempt
                    ? "前回の応答ではツール呼び出しが実行されませんでした (<tool_call> をテキストとして書いたため)。" +
                      "ツールは必ず通常の function calling 機能で呼んでください (テキストに書かない)。" +
                      "どうしてもテキストで表現する場合は <tool_call>{\"name\":\"ツール名\",\"arguments\":{…}}</tool_call> の形で JSON を必ず含めること。"
                    : "まだ完了していません。思考だけで応答を終えないこと。必要なツールを呼ぶ" +
                      "(結果をアプリに出すタスクなら open_thread_list_in_app / open_thread_in_app / open_board_in_app を実際に呼ぶ)" +
                      "か、本当に完了したなら submit_result(status, finding) を必ず呼んでください。"));
                continue;
            }

            // 進捗があったので停滞カウンタをリセット。
            emptyRounds = 0;

            // assistant の tool_calls ラウンドを履歴に記録 (思考過程は残さない)。
            messages.Add(new("assistant", text) { ToolCalls = result.ToolCalls });

            foreach (var tc in result.ToolCalls)
            {
                // 終端ツール: submit_result。
                if (tc.Name == "submit_result")
                    return ParseSubmit(spec, tc.ArgumentsJson, evidenceIds, toolCalls);

                // 予算超過: 実行しない (このラウンドの後でまとめさせる)。
                if (toolCalls >= maxCalls)
                {
                    section.ToolMarker($"{tc.Name} (予算超過で拒否)", failed: true);
                    messages.Add(new("tool", ErrorJson("ツール予算の上限に達しました。これ以上ツールは呼べません。")) { ToolCallId = tc.Id });
                    stopReason = $"ツール予算 ({maxCalls} 回) を使い切った";
                    continue;
                }

                // 同じ引数での呼び直し: 実行せず、結果は既に履歴にあると返す (回数は消費しない)。
                var key = tc.Name + "\u0001" + NormalizeArgs(tc.ArgumentsJson);
                if (done.Contains(key))
                {
                    section.ToolMarker($"{FormatLabel(tc)} (同じ呼び出しのため省略)", failed: true);
                    messages.Add(new("tool", ErrorJson(
                        "同じツールを同じ引数で既に呼んでいます。結果は上の履歴にあります (省略されている場合は recall_archive で引ける)。" +
                        "別の引数で呼ぶか、集めた情報で submit_result してください。")) { ToolCallId = tc.Id });
                    continue;
                }

                // 通常のツール実行 (L1 ToolRuntime 経由)。
                var output = await _runtime.ExecuteAsync(tc.Name, tc.ArgumentsJson, spec.Id, outputCap, ct).ConfigureAwait(true);
                toolCalls++;
                section.ToolMarker(FormatLabel(tc), failed: !output.Ok);
                if (output.ArchiveId is not null) evidenceIds.Add(output.ArchiveId);
                if (output.Ok) done.Add(key);   // 失敗した呼び出しは引数を直して呼び直せるよう記録しない

                var content = output.Ok
                    ? (output.NormalizedResult ?? "")
                    : (output.StructuredError ?? ErrorJson("不明なツールエラー"));
                content += BudgetNote(maxCalls - toolCalls);
                messages.Add(new("tool", content) { ToolCallId = tc.Id });
                archiveOf[tc.Id] = (tc.Name, output.ArchiveId);
            }

            // 予算をちょうど使い切ったら、次のラウンドを待たずにまとめへ。
            if (stopReason is null && toolCalls >= maxCalls)
                stopReason = $"ツール予算 ({maxCalls} 回) を使い切った";
        }

        stopReason ??= "ラウンド上限に達した";
        return await SummarizeAndStopAsync(spec, section, messages, archiveOf, stopReason, stagnated,
                                           evidenceIds, toolCalls, ct).ConfigureAwait(true);
    }

    /// <summary>打ち切り時のまとめ: ツールを submit_result だけにして 1 回呼び、ここまでの情報を finding にさせる。
    /// 停滞による打ち切りでは done を信用せず partial に落とす (= 「表示しました」等の誤報告を避ける)。</summary>
    private async Task<TaskResult> SummarizeAndStopAsync(
        TaskSpec spec, IWorkSection section, List<LlmChatMessage> messages,
        Dictionary<string, (string Name, string? ArchiveId)> archiveOf,
        string reason, bool stagnated, List<string> evidenceIds, int toolCalls, CancellationToken ct)
    {
        messages.Add(new("user",
            $"打ち切り: {reason}。これ以上ツールは使えません。ここまでに集めた情報だけで submit_result を呼んで終了してください。" +
            "目標を満たせていなければ status=partial とし、finding には分かったこと (具体的に) と、未達の理由・未調査の範囲を書くこと。"));
        var submitOnly = new List<object> { SubmitResultToolDef() };
        Compact(messages, archiveOf, ContextBudget.InputBudget(_settings, ContextBudget.EstimateToolDefs(submitOnly)));

        // tool_choice=required で submit_result を強制する。対応しないサーバでエラーになったら指定なしで再試行。
        var result = await _llm.ChatStreamAsync(_settings, messages, section.Stream, submitOnly, ct, RequireTool).ConfigureAwait(true);
        if (!result.Ok)
            result = await _llm.ChatStreamAsync(_settings, messages, section.Stream, submitOnly, ct).ConfigureAwait(true);

        TaskResult r;
        if (!result.Ok)
            r = new TaskResult(spec.Id, TaskOutcome.Partial, $"{reason}ため打ち切り (まとめの生成にも失敗: {result.Error})。", evidenceIds, toolCalls);
        else if (result.ToolCalls.FirstOrDefault(c => c.Name == "submit_result") is { } submit)
            r = ParseSubmit(spec, submit.ArgumentsJson, evidenceIds, toolCalls);
        else
        {
            var text = ContextBudget.StripThink(result.Content).Trim();
            r = new TaskResult(spec.Id, TaskOutcome.Partial,
                text.Length > 0 ? text : $"{reason}ため打ち切り (まとめは得られなかった)。", evidenceIds, toolCalls);
        }
        if (stagnated && r.Status == TaskOutcome.Done) r = r with { Status = TaskOutcome.Partial };
        return r.Finding.StartsWith($"{reason}ため", System.StringComparison.Ordinal)
            ? r
            : r with { Finding = $"[{reason}ため打ち切り] " + r.Finding };
    }

    /// <summary>文脈が入力枠を超えそうなら縮める。
    /// <list type="number">
    /// <item><description>古い tool 結果を省略の案内に置き換える (直近 <see cref="KeepRecentToolResults"/> 件は残す。収まらなければ最新 1 件だけ残す)。</description></item>
    /// <item><description>それでも超えるなら、古いツール呼び出しの往復 (assistant の tool_calls + その結果) を丸ごと外し、
    ///   「これまでに呼んだツール (archive id)」の一覧だけを 1 件のメモとして残す (最後の往復は残す)。</description></item>
    /// </list></summary>
    private static void Compact(List<LlmChatMessage> messages,
                                Dictionary<string, (string Name, string? ArchiveId)> archiveOf, int budget)
    {
        if (ContextBudget.EstimateTokens(messages) <= budget) return;

        string Label(string? toolCallId)
        {
            var (name, id) = toolCallId is not null && archiveOf.TryGetValue(toolCallId, out var a) ? a : ("?", null);
            return id is null ? name : $"{name} (archive id {id})";
        }
        string Placeholder(int i, string _)
        {
            var id = messages[i].ToolCallId is { } tc && archiveOf.TryGetValue(tc, out var a) ? a.ArchiveId : null;
            return $"文脈節約のため古い {Label(messages[i].ToolCallId)} の結果を省略した。" +
                   (id is null ? "必要なら同じツールを呼び直すこと。" : $"必要なら recall_archive(id=\"{id}\") で引き戻せる。");
        }

        // 1) 古い tool 結果を案内に置き換える
        var tools = messages.Select((m, i) => (m, i)).Where(x => x.m.Role == "tool").Select(x => x.i).ToList();
        for (var keep = KeepRecentToolResults; keep >= 1; keep--)
        {
            if (tools.Count <= keep) continue;
            ContextBudget.CompactToolResults(messages, budget, tools[^keep], Placeholder);
            if (ContextBudget.EstimateTokens(messages) <= budget) return;
        }

        // 2) 古い往復を丸ごと外して、呼んだツールの一覧のメモにまとめる (system [0] と タスク [1] は残す)
        var dropped = new List<string>();
        while (ContextBudget.EstimateTokens(messages) > budget)
        {
            var lastRound = messages.FindLastIndex(m => m.Role == "assistant" && m.ToolCalls is { Count: > 0 });
            var first     = messages.FindIndex(2, m => m.Role == "assistant" && m.ToolCalls is { Count: > 0 });
            if (first < 0 || first >= lastRound) break;
            var end = first + 1;
            while (end < messages.Count && messages[end].Role == "tool") end++;
            foreach (var tc in messages[first].ToolCalls!) dropped.Add(Label(tc.Id));
            messages.RemoveRange(first, end - first);
        }
        if (dropped.Count == 0) return;
        const string NoteHead = "[省略] 文脈節約のため、これまでのツール呼び出しの一部を履歴から外した。外した呼び出し: ";
        if (messages.Count > 2 && messages[2].Role == "user" && messages[2].Content.StartsWith(NoteHead, System.StringComparison.Ordinal))
            messages[2] = messages[2] with { Content = messages[2].Content.TrimEnd('。') + ", " + string.Join(", ", dropped) + "。" };
        else
            messages.Insert(2, new("user", NoteHead + string.Join(", ", dropped) +
                "。結果が必要なら recall_archive(id) で引き戻せる。同じ呼び出しを繰り返さないこと。"));
    }

    // ---- helpers ----

    private static TaskResult Finish(IWorkSection section, TaskResult result)
    {
        section.Complete(result.Status, result.Finding);
        return result;
    }

    /// <summary>ツール結果の末尾に付ける残り回数の注記。</summary>
    private static string BudgetNote(int remaining) => remaining switch
    {
        <= 0 => "\n[ツール残り 0 回: 次は submit_result を呼ぶこと]",
        1    => "\n[ツール残り 1 回: 足りなければ今ある情報で submit_result(status=partial) を呼ぶこと]",
        _    => $"\n[ツール残り {remaining} 回]",
    };

    /// <summary>引数 JSON を比較用に正規化する (空白・キー順の違いを吸収)。壊れた JSON はそのまま。</summary>
    private static string NormalizeArgs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "{}";
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Canon(doc.RootElement);
        }
        catch { return json.Trim(); }

        static string Canon(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject().OrderBy(p => p.Name, System.StringComparer.Ordinal)
                                                             .Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canon(p.Value))) + "}",
            JsonValueKind.Array  => "[" + string.Join(",", e.EnumerateArray().Select(Canon)) + "]",
            _                    => e.GetRawText(),
        };
    }

    private static string BuildTaskPrompt(TaskSpec spec)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(spec.UserRequest))
            sb.Append("# ユーザの依頼 (原文・参考)\n").Append(spec.UserRequest.Trim()).Append("\n")
              .Append("(あなたの担当は下の「タスク」だけ。依頼の他の部分は別のタスクが受け持つことがある。意図の解釈に使うこと)\n\n");
        sb.Append("# タスク\n").Append(spec.Goal).Append('\n');
        if (!string.IsNullOrWhiteSpace(spec.ContextHint))
            sb.Append("\n# 文脈ヒント (Strategist から)\n").Append(spec.ContextHint).Append('\n');
        sb.Append("\n# 制約\n")
          .Append($"- ツール呼び出しは最大 {System.Math.Max(1, spec.Limits.MaxToolCalls)} 回。各ツール結果の末尾に残り回数が付く。\n")
          .Append("- 終わったら submit_result を呼ぶ (status と finding は必須。finding の書き方は system の「報告」に従う)。\n");
        return sb.ToString();
    }

    private static TaskResult ParseSubmit(TaskSpec spec, string? argsJson, List<string> evidenceIds, int toolCalls)
    {
        var status  = TaskOutcome.Done;
        var finding = "";
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String)
                    status = ParseOutcome(s.GetString());
                if (root.TryGetProperty("finding", out var f) && f.ValueKind == JsonValueKind.String)
                    finding = f.GetString() ?? "";
            }
        }
        catch { /* 壊れた JSON は done + 空 finding 扱い (下で補完) */ }

        if (string.IsNullOrWhiteSpace(finding)) finding = "(finding 未記入)";
        return new TaskResult(spec.Id, status, finding.Trim(), evidenceIds, toolCalls);
    }

    private static TaskOutcome ParseOutcome(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "partial" => TaskOutcome.Partial,
        "failed"  => TaskOutcome.Failed,
        _         => TaskOutcome.Done,
    };

    private static string FormatLabel(LlmToolCall tc)
    {
        var a = tc.ArgumentsJson ?? "";
        if (a.Length > 80) a = a.Substring(0, 80) + "…";
        return $"{tc.Name}({a})";
    }

    private static object SubmitResultToolDef() => new
    {
        type     = "function",
        function = new
        {
            name        = "submit_result",
            description = "タスクの完了を報告して終了する。これを呼ぶとこのタスクは終わる。finding は Strategist がそれだけを見て最終回答を書く報告なので、" +
                          "答えの材料を具体的に書くこと (書き方は system の「報告」)。",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    status  = new
                    {
                        type        = "string",
                        @enum       = new[] { "done", "partial", "failed" },
                        description = "done (達成) / partial (部分達成・予算切れ等) / failed (達成不能) のいずれか。",
                    },
                    finding = new
                    {
                        type        = "string",
                        description = "報告 (日本語)。結論と答えの材料・根拠 (レス番号 / スレ URL / 短い引用)・アプリに表示したもの・未達ならその理由。" +
                                      "長さは内容に見合う量 (目安 200〜1500 字)。",
                    },
                },
                required = new[] { "status", "finding" },
            },
        },
    };

    /// <summary>まとめのラウンドで submit_result を強制する追加パラメータ。</summary>
    private static readonly IReadOnlyDictionary<string, object?> RequireTool = new Dictionary<string, object?> { ["tool_choice"] = "required" };

    /// <summary>本文に紛れ込んだ <c>&lt;tool_call&gt;</c> / <c>&lt;/tool_call&gt;</c> マーカー検出用
    /// (= ツール呼び出しをテキストで書いてしまい未実行のラウンドを done にしないため)。</summary>
    private static readonly Regex TextToolMarkerRe = new(@"</?tool_call\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string ErrorJson(string message)
        => JsonSerializer.Serialize(new { error = message }, JsonOpts);

    /// <summary>Worker の既定 system プロンプト。engine がスレの状況などの背景を後ろに付けて使う。</summary>
    public const string DefaultSystemPrompt =
        "あなたは掲示板ブラウザ内蔵 AI のタスク実行担当 (Worker) です。Strategist から渡された 1 つのタスクだけを、ツールを使って遂行し、結果を報告します。" +
        "あなたはユーザと対話できません (確認や質問はできない)。判断は自分で行います。\n" +
        "\n" +
        "# 進め方\n" +
        "1. タスク・文脈ヒント・ユーザの依頼原文から、何を持ち帰れば完了かを決める。\n" +
        "2. 必要な情報だけをツールで集める。ツールは必ず function calling で呼ぶ (本文に <tool_call> 等をテキストで書いても実行されない)。互いに独立な呼び出しは 1 回の応答でまとめて呼んでよい。\n" +
        "3. 集め終えたら submit_result を呼ぶ。テキストだけで終わらない。\n" +
        "\n" +
        "# ツールの使い方\n" +
        "- 各ツール結果の末尾に残り回数が付く。残りが少なくなったら、それまでの情報で submit_result する (足りなければ status=partial)。\n" +
        "- スレの中身は get_thread_meta → get_posts / search_posts / find_popular_posts。レス番号は掲示板により連番とは限らないので get_thread_meta の first_post_number / last_post_number を見る。\n" +
        "- 板を探すときは list_boards を keyword で絞る。テーマで複数の板を探すときは、まず keyword なしでカテゴリ一覧を取り、関連カテゴリを categories に指定して板を取る。" +
        "したらば / reddit の板は search_boards で探す。\n" +
        "- 一覧系 (list_boards / search_boards / list_threads) は keyword / limit で絞る。同じ引数で呼び直さない (呼び直しは実行されない)。\n" +
        "- 結果が長すぎると途中で切れる。切れたら引数 (範囲・keyword・limit) を絞って取り直す。古い結果は文脈節約のため省略されることがあり、必要なら recall_archive で引き戻せる。\n" +
        "- 板をまたいで調べるときは、残り回数に収まる板数に絞る。\n" +
        "- 不明・曖昧な語: 文脈ヒントに別名・関連語・対象板があればまずそれを使う。無く、自分でも確信が持てないとき (略称 / 新語 / 最初の検索が空振り) だけ、" +
        "web_search で正体を特定し『正式名称・略称・関連語 (キャラ名 / 作者等)』に広げてから探す。スレタイは略称・関連語で立つことが多い " +
        "(例: 5ch でダンジョン飯 → \"ダン飯\" / 作者 \"九井諒子\")。1 語で空振りしても別の語・別の板で探し直す。\n" +
        "- タスクが結果を『アプリに表示 / 開く』ことを含むなら、調べて終わりにせず open_thread_list_in_app (複数) / open_thread_in_app (単一) / open_board_in_app (板) を実際に呼んでから報告する。\n" +
        "\n" +
        "# 報告 (submit_result の finding)\n" +
        "Strategist はあなたの finding だけを見てユーザへの回答を書く (ツール結果の原文は見えない)。次を具体的に書くこと:\n" +
        "- 結論と答えの材料: 要約・説明のタスクなら要約本文そのもの。話題・意見の傾向・数字・固有名詞は省かずに書く。\n" +
        "- 根拠: レス番号・スレのタイトルと URL・短い引用。\n" +
        "- アプリに表示した場合: 何を、どのタブに、何件。\n" +
        "- 未達 (partial / failed) の場合: 理由と、調べられなかった範囲。\n" +
        "長さは内容に見合う量 (目安 200〜1500 字)。推測は推測と明記する。";
}
