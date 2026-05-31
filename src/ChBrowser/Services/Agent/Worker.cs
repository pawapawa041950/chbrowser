using System.Text;
using System.Text.Json;
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
/// <para>停止条件 (D9): <c>submit_result</c> 呼び出し (done/partial/failed) / ツール予算
/// (<see cref="Limits.MaxToolCalls"/>) 超過 → partial / ラウンド上限 → partial / LLM 失敗 → failed。
/// reasoning モデルが <c>submit_result</c> を呼び忘れてテキストだけ返した場合は「done の finding」とみなす
/// 寛容フォールバック (D10 終端の堅牢性)。</para></summary>
public sealed class Worker
{
    private readonly LlmClient   _llm;
    private readonly LlmSettings _settings;     // Worker 接続 (D7)
    private readonly ToolRuntime _runtime;
    private readonly string      _systemPrompt;

    public Worker(LlmClient llm, LlmSettings workerSettings, ToolRuntime runtime, string? systemPrompt = null)
    {
        _llm          = llm;
        _settings     = workerSettings;
        _runtime      = runtime;
        _systemPrompt = string.IsNullOrWhiteSpace(systemPrompt) ? DefaultSystemPrompt : systemPrompt!;
    }

    /// <summary>タスクを 1 つ実行して <see cref="TaskResult"/> を返す。作業ログは <paramref name="section"/> に流す。</summary>
    public async Task<TaskResult> RunAsync(TaskSpec spec, IWorkSection section, CancellationToken ct)
    {
        var messages = new List<LlmChatMessage>
        {
            new("system", _systemPrompt),
            new("user",   BuildTaskPrompt(spec)),
        };
        var toolDefs = new List<object>(_runtime.GetWorkerToolDefinitions()) { SubmitResultToolDef() };

        var maxCalls    = System.Math.Max(1, spec.Limits.MaxToolCalls);
        var maxRounds   = maxCalls + 8;   // think-only / 予算超過の促し に余裕を持たせる
        var toolCalls   = 0;
        var evidenceIds = new List<string>();
        var emptyRounds = 0;              // 「ツールも本文も無い (= think だけ)」停滞ラウンドの連続数
        const int MaxEmptyRounds = 3;     // これを超えたら partial で打ち切り (= 誤って done にしない)

        for (var round = 0; round < maxRounds; round++)
        {
            var result = await _llm.ChatStreamAsync(_settings, messages, section.Stream, toolDefs, ct).ConfigureAwait(true);

            // LLM 失敗 → failed TaskResult (= Strategist が処理する / D15)。
            if (!result.Ok)
                return Finish(section, new TaskResult(spec.Id, TaskOutcome.Failed,
                    $"Worker の LLM 呼び出しに失敗: {result.Error}", evidenceIds, toolCalls));

            // ツールなしラウンド。
            if (result.ToolCalls.Count == 0)
            {
                var (text, _) = ChatArchive.SplitThink(result.Content ?? "");
                // 本文がある = 最終回答テキストを submit_result の代わりに返した寛容ケース → done (D10)。
                if (!string.IsNullOrWhiteSpace(text))
                    return Finish(section, new TaskResult(spec.Id, TaskOutcome.Done, text.Trim(), evidenceIds, toolCalls));

                // 本文が空 (= 思考だけ / 完全に空) の停滞ラウンドは done にしない。
                // ここを done 扱いにすると「open_*_in_app を呼ぶ前に finding『(本文なし)』で完了」して
                // 「表示しました」と誤報告する原因になる。継続を促し、続くようなら partial で正直に返す。
                emptyRounds++;
                if (emptyRounds >= MaxEmptyRounds)
                    return Finish(section, new TaskResult(spec.Id, TaskOutcome.Partial,
                        "ツールも本文も無いラウンド (思考のみ) が続いたため打ち切り。タスクは未達 — 必要なツール呼び出し / アプリ反映ができていない。",
                        evidenceIds, toolCalls));

                messages.Add(new("assistant", result.Content ?? ""));
                messages.Add(new("user",
                    "まだ完了していません。思考だけで応答を終えないこと。必要なツールを呼ぶ" +
                    "(結果をアプリに出すタスクなら open_thread_list_in_app / open_thread_in_app / open_board_in_app を実際に呼ぶ)" +
                    "か、本当に完了したなら submit_result(status, finding) を必ず呼んでください。"));
                continue;
            }

            // 進捗があったので停滞カウンタをリセット。
            emptyRounds = 0;

            // assistant の tool_calls ラウンドを履歴に記録。
            messages.Add(new("assistant", result.Content ?? "") { ToolCalls = result.ToolCalls });

            foreach (var tc in result.ToolCalls)
            {
                // 終端ツール: submit_result。
                if (tc.Name == "submit_result")
                    return Finish(section, ParseSubmit(spec, tc.ArgumentsJson, evidenceIds, toolCalls));

                // 予算超過: 実行せず拒否し、submit_result(partial) を促す。
                if (toolCalls >= maxCalls)
                {
                    section.ToolMarker($"{tc.Name} (予算超過で拒否)", failed: true);
                    messages.Add(new("tool",
                        ErrorJson("ツール予算の上限に達しました。これ以上ツールは呼べません。今ある情報で submit_result(status=partial) を呼んでください。"))
                        { ToolCallId = tc.Id });
                    continue;
                }

                // 通常のツール実行 (L1 ToolRuntime 経由)。
                var output = await _runtime.ExecuteAsync(tc.Name, tc.ArgumentsJson, spec.Id, ct).ConfigureAwait(true);
                toolCalls++;
                section.ToolMarker(FormatLabel(tc), failed: !output.Ok);
                if (output.ArchiveId is not null) evidenceIds.Add(output.ArchiveId);

                var content = output.Ok
                    ? (output.NormalizedResult ?? "")
                    : (output.StructuredError ?? ErrorJson("不明なツールエラー"));
                messages.Add(new("tool", content) { ToolCallId = tc.Id });
            }
        }

        // ラウンド上限: 収集済みの範囲で partial 合成 (= 必ず止まる / D9)。
        return Finish(section, new TaskResult(spec.Id, TaskOutcome.Partial,
            "ラウンド上限に達したため打ち切りました (収集済みの範囲での部分的な結果)。", evidenceIds, toolCalls));
    }

    // ---- helpers ----

    private static TaskResult Finish(IWorkSection section, TaskResult result)
    {
        section.Complete(result.Status, result.Finding);
        return result;
    }

    private static string BuildTaskPrompt(TaskSpec spec)
    {
        var sb = new StringBuilder();
        sb.Append("# タスク\n").Append(spec.Goal).Append('\n');
        if (!string.IsNullOrWhiteSpace(spec.ContextHint))
            sb.Append("\n# 文脈ヒント\n").Append(spec.ContextHint).Append('\n');
        sb.Append("\n# 制約\n")
          .Append($"- ツール呼び出しは最大 {System.Math.Max(1, spec.Limits.MaxToolCalls)} 回。\n")
          .Append("- 完了したら必ず submit_result を呼ぶこと (status と finding は必須)。\n");
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
            description = "タスクの完了を報告して終了する。これを呼ぶとこのタスクは終わる。集め終えたら必ず呼ぶこと。",
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
                        description = "タスク結果の要約 (3 文程度・日本語)。done 以外なら『なぜ達成できなかったか』も書く。",
                    },
                },
                required = new[] { "status", "finding" },
            },
        },
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string ErrorJson(string message)
        => JsonSerializer.Serialize(new { error = message }, JsonOpts);

    /// <summary>Worker の既定 system プロンプト。engine が必要に応じてスレ文脈入りのものに差し替える (B4)。</summary>
    public const string DefaultSystemPrompt =
        "あなたはタスク実行ワーカー (Worker) です。与えられた 1 つのタスクだけを、ツールを使って遂行します。\n" +
        "\n" +
        "ルール:\n" +
        "- 提供された読み取りツールで、タスク達成に必要な情報「だけ」を集める。無駄なツール呼び出しはしない。\n" +
        "- 集め終えたら必ず submit_result を呼んで終了する。status は done/partial/failed、finding は結果の要約 (3 文程度・日本語)。\n" +
        "- **ツール予算 (上記の最大回数) を厳守する。** 予算を使い切る前に submit_result を呼ぶこと。残り 1 回になったら必ず submit_result(status=partial) で締める。\n" +
        "- 一覧系ツール (list_boards / list_threads) はできるだけ keyword や limit で絞る (全件取得は文脈を圧迫し予算も消費する)。既に取得済みの一覧を同じ引数で取り直さない。\n" +
        "- 板をまたいで調べる場合は board ごとに list_threads を 1 回ずつ回してよいが、予算内に収まる板数に絞ること (収まらなければ partial で「どこまで調べたか」を finding に書く)。\n" +
        "- **タスクが結果を『アプリに表示 / 開く』ことを含む場合は、調べて終わりにせず、open_thread_list_in_app (複数) / open_thread_in_app (単一) / open_board_in_app (板) で実際にアプリのペインに反映してから submit_result する。** テキスト報告だけで済ませない。finding には「何件をどのタブに出したか」を書く。\n" +
        "- **不明・曖昧な語のグラウンディング ★重要:** 依頼の中心となる固有名詞・略称・作品/製品/人物名などは、推測で 5ch 検索に突っ込まない。" +
        "正式名称・別名(略称 / 英語表記)・主要な関連語(キャラ名 / 作者 / シリーズ / メーカー / 型番 等)を**確信を持って**挙げられるならそのまま使ってよいが、" +
        "少しでも怪しい(略称や新語に見える / 自分の知識が古い可能性 / 解釈が複数ありうる / 最初のスレ検索が空振りした)なら、" +
        "まず web_search で正体を特定する(必要なら web_fetch で裏取り)。" +
        "特定できたら『正式名称＋よく使う略称＋関連語』に展開し、複数の語で板 / スレを横断検索する。" +
        "5ch のスレタイは略称・関連語で立つことが多い(例: ダンジョン飯 → \"ダン飯\" / 作者 \"九井諒子\" / キャラ名)。" +
        "1 語で空振りしても別語・別板で再検索する(空振り = 語が足りない信号)。" +
        "推測で空振りするより先に 1〜2 回 web で地ならしする方が結局速い。ただし既によく知っている語をいちいち調べ直さない(予算の無駄)。\n" +
        "- 達成不能と判断したら submit_result(status=failed) を呼び、finding に理由を書く。\n" +
        "- あなたはユーザと対話できない (確認や質問はできない)。判断は自分で行う。\n" +
        "- 思考は簡潔に。";
}
