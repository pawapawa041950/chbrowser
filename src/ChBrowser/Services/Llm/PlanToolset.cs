using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ChBrowser.Services.Llm;

/// <summary>plan の 1 タスクの状態。</summary>
public enum PlanTaskStatus { Pending, Completed }

/// <summary>plan の 1 タスク。<see cref="Finding"/> は <see cref="PlanTaskStatus.Completed"/> 後の知見メモ。</summary>
public sealed class PlanTask
{
    public string             Id          { get; set; } = "";
    public string             Description { get; set; } = "";
    public PlanTaskStatus     Status      { get; set; } = PlanTaskStatus.Pending;
    /// <summary>complete_task で LLM が残した「このタスクで分かったこと」のメモ。</summary>
    public string?            Finding     { get; set; }
}

/// <summary>
/// LLM (= AI チャット内のエージェント) に「タスク計画 → 実行 → 完了報告 → 必要なら計画修正」という
/// 段取りを踏ませるためのメタツール群。指示追従性を上げるための仕組みで、Google の TTD-DR
/// (Test-Time Diffusion Deep Researcher) 系の plan-revise パターンに基づく。
///
/// 提供するツール:
/// <list type="bullet">
///   <item><c>create_plan(tasks)</c> — 最初に呼び、計画を宣言する。</item>
///   <item><c>complete_task(id, finding)</c> — 1 タスク終わるたびに呼び、知見メモを残す。</item>
///   <item><c>revise_plan(tasks)</c> — 当初想定が外れたら計画を書き直す (= 既存 id は status/finding を保持)。</item>
/// </list>
///
/// 各ツールの応答には常に「現在の plan 全体 + 完了数」が入る。LLM はこれを見て「あと何が残っているか」を
/// 把握できる。C# 側は <see cref="PlanChanged"/> で UI に通知する (= ステータスバーの進捗表示用)。
/// </summary>
public sealed class PlanToolset
{
    private readonly List<PlanTask> _tasks = new();

    /// <summary>現在の plan (= タスク一覧)。UI から読み取り用。書き換えはツール経由のみ。</summary>
    public IReadOnlyList<PlanTask> Tasks => _tasks;

    /// <summary>完了済みタスク数。</summary>
    public int CompletedCount => _tasks.Count(t => t.Status == PlanTaskStatus.Completed);

    /// <summary>計画が変わったとき (= 作成 / 完了 / 修正) に発火。UI スレッドで Execute されるので UI スレッドで発火する。</summary>
    public event Action? PlanChanged;

    /// <summary>create_plan の直後で「コスト見積りがまだ」の状態。<see cref="EstimateAndConfirm"/> を呼ぶと false に戻る。
    /// AiChatViewModel の DispatchToolAsync がこのフラグを見て、true の間は他ツール呼び出しを差し止める
    /// (= AI に「先に estimate_and_confirm を呼べ」と矯正する仕組み)。</summary>
    public bool EstimateRequired { get; private set; }

    /// <summary>直前のユーザメッセージが「作業承諾」を意味すると LLM 分類器が判定した場合に AiChatViewModel から立てるフラグ。
    /// 次の <see cref="EstimateAndConfirm"/> は heavy 判定をスキップして強制 light として通す
    /// (= ユーザが既に承諾したのに再度確認させない、無限確認ループ防止)。1 回使うと自動で降りる。</summary>
    private bool _userPreApproved;

    /// <summary>AiChatViewModel.SendAsync 冒頭で「ユーザの承諾応答を検出した」場合に呼ぶ。
    /// 次の estimate_and_confirm が強制 light で通る (= 確認ループの中断点)。</summary>
    public void MarkUserPreApproved() => _userPreApproved = true;

    /// <summary>直前ラウンドで <see cref="EstimateAndConfirm"/> が heavy 判定を出して AI が確認文をユーザに提示中の状態。
    /// このフラグが立っている間に来た次のユーザ送信については、AiChatViewModel が LLM 分類器を起動して
    /// 「承諾応答か / 新規依頼か」を判定する。estimate_and_confirm が再度呼ばれた時点で false に戻る。</summary>
    public bool HeavyConfirmationPresented { get; private set; }

    /// <summary>create_plan / revise_plan / complete_task / estimate_and_confirm は <see cref="EstimateRequired"/> ガードを
    /// バイパスして良いツール。これらは plan の状態を整える側なので、見積り前でも呼ばせる。</summary>
    public static bool ToolBypassesEstimateGate(string toolName) => toolName switch
    {
        "create_plan" or "revise_plan" or "complete_task" or "estimate_and_confirm" => true,
        _ => false,
    };

    /// <summary>OpenAI 互換 <c>tools</c> 配列。 <see cref="ThreadToolset.GetToolDefinitions"/> と合わせて
    /// LLM に提示する。</summary>
    public IReadOnlyList<object> GetToolDefinitions()
    {
        return new object[]
        {
            new
            {
                type     = "function",
                function = new
                {
                    name        = "create_plan",
                    description = "ユーザの依頼を達成するためのタスク列を宣言する。必ず最初に呼ぶこと。単純な依頼なら 1 タスクでよい。各タスクは「何を調べて何を確認するか」が明確になるよう書く。",
                    parameters  = new
                    {
                        type       = "object",
                        properties = new
                        {
                            tasks = new
                            {
                                type        = "array",
                                description = "実行するタスクの順序付きリスト。",
                                items = new
                                {
                                    type       = "object",
                                    properties = new
                                    {
                                        id          = new { type = "string", description = "タスクの短い識別子 (例: \"t1\", \"t2\")。complete_task で参照する。" },
                                        description = new { type = "string", description = "このタスクで何をするかの説明 (= 何を取得して何を確認するか)。" },
                                    },
                                    required = new[] { "id", "description" },
                                },
                            },
                        },
                        required = new[] { "tasks" },
                    },
                },
            },
            new
            {
                type     = "function",
                function = new
                {
                    name        = "complete_task",
                    description = "1 タスクを完了として記録する。スレッド読み取りツールで必要な情報を得たあとに呼ぶ。finding には「このタスクで分かったこと」を短く要約して書く (= 後で最終回答を組み立てる際の根拠になる)。",
                    parameters  = new
                    {
                        type       = "object",
                        properties = new
                        {
                            id      = new { type = "string", description = "完了するタスクの id (= create_plan で指定したもの)。" },
                            finding = new { type = "string", description = "このタスクで得た知見の要約 (1〜3 文程度)。" },
                        },
                        required = new[] { "id", "finding" },
                    },
                },
            },
            new
            {
                type     = "function",
                function = new
                {
                    name        = "estimate_and_confirm",
                    description = "**create_plan / revise_plan の直後に必ず呼ぶ義務化ツール**。" +
                                  "立てた plan を実行する場合の見積りコストをサーバに伝え、サーバが「重い / 軽い」を判定する。" +
                                  "サーバ判定が heavy なら、次のラウンドでツール呼び出しせず確認文を最終回答テキストとして出力すること (= エージェントループ一旦停止 / ユーザの返答待ち)。" +
                                  "light ならそのまま plan 実行に進む。" +
                                  "estimate を呼ばずに他ツール (= list_*, get_*, find_*, open_*, search_*) を呼ぶとガードでエラー JSON が返るので必ず先に呼ぶこと。",
                    parameters  = new
                    {
                        type       = "object",
                        properties = new
                        {
                            estimated_tool_calls = new
                            {
                                type        = "integer",
                                description = "残り plan 実行で見込まれるツール呼び出し総回数 (= list_* / get_* / find_* / search_* / open_* など、estimate_and_confirm 自身は除外)。例: 板 scan 1 + スレ scan 10 + open 1 = 12。",
                            },
                            scan_breadth = new
                            {
                                type        = "string",
                                description = "スキャン幅。\"single\" (= 単一板または単一スレ内) / \"few\" (= 2-4 板) / \"many\" (= 5+ 板) / \"all_boards\" (= 全板 scan)。",
                            },
                            reads_full_thread = new
                            {
                                type        = "boolean",
                                description = "1 スレで 200 レス超を読み通す必要があるか (= get_posts を 4 回以上分割呼びする予定)。",
                            },
                            plan_summary = new
                            {
                                type        = "string",
                                description = "現 plan の自然文要約 (1〜2 文)。heavy 判定時にこの文章を確認文に流用する。",
                            },
                            lighter_alternative = new
                            {
                                type        = "string",
                                description = "heavy になりそうなら、軽量代替案を 1 つ書く (例: 「板を 3 つに絞る」「最初の 30 レスだけ読む」「keyword 検索で済ます」)。light なら空文字可。",
                            },
                        },
                        required = new[] { "estimated_tool_calls", "scan_breadth", "reads_full_thread", "plan_summary" },
                    },
                },
            },
            new
            {
                type     = "function",
                function = new
                {
                    name        = "revise_plan",
                    description = "計画を書き換える。当初想定が外れたとき (= 過剰だった、足りなかった、順序を変えたい、新たな調査が必要、など) に使う。既存タスクと同じ id を含めればその status/finding は引き継がれる。新規 id のタスクは pending として追加される。応答に含まれない id のタスクは削除される。",
                    parameters  = new
                    {
                        type       = "object",
                        properties = new
                        {
                            tasks = new
                            {
                                type        = "array",
                                description = "新しいタスクの順序付きリスト (= 既存 plan を完全に置き換える)。",
                                items = new
                                {
                                    type       = "object",
                                    properties = new
                                    {
                                        id          = new { type = "string" },
                                        description = new { type = "string" },
                                    },
                                    required = new[] { "id", "description" },
                                },
                            },
                        },
                        required = new[] { "tasks" },
                    },
                },
            },
        };
    }

    /// <summary>LLM が呼んできたツールを実行。未知ツールは null を返す (= ディスパッチ側で別の toolset を試す合図)。</summary>
    public string? TryExecute(string name, string argumentsJson)
    {
        try
        {
            return name switch
            {
                "create_plan"          => CreatePlan(argumentsJson),
                "complete_task"        => CompleteTask(argumentsJson),
                "revise_plan"          => RevisePlan(argumentsJson),
                "estimate_and_confirm" => EstimateAndConfirm(argumentsJson),
                _                      => null,
            };
        }
        catch (Exception ex)
        {
            return ErrorJson($"plan ツール実行で例外: {ex.Message}");
        }
    }

    // ---- ツール実装 ----

    private string CreatePlan(string argsJson)
    {
        if (!TryParseTasksArg(argsJson, out var newTasks, out var err))
            return ErrorJson(err);

        _tasks.Clear();
        foreach (var t in newTasks) _tasks.Add(t);
        EstimateRequired = true; // 次は estimate_and_confirm を呼ぶ義務
        PlanChanged?.Invoke();
        return SerializePlan(
            message: $"計画を作成しました ({_tasks.Count} タスク)。**次のステップ: estimate_and_confirm を必ず呼んでコスト見積りしてから他ツールに進むこと**。");
    }

    private string CompleteTask(string argsJson)
    {
        if (!TryParseObject(argsJson, out var args))
            return ErrorJson("引数 JSON のパースに失敗");
        if (!args.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
            return ErrorJson("id が指定されていません");
        var id = idEl.GetString() ?? "";
        if (id.Length == 0) return ErrorJson("id が空です");

        var finding = args.TryGetProperty("finding", out var fEl) && fEl.ValueKind == JsonValueKind.String
            ? (fEl.GetString() ?? "") : "";

        var task = _tasks.FirstOrDefault(t => t.Id == id);
        if (task is null)
        {
            var valid = string.Join(", ", _tasks.Select(t => $"\"{t.Id}\""));
            return ErrorJson($"id=\"{id}\" のタスクが見つかりません。現在の plan の id: [{valid}]");
        }
        task.Status  = PlanTaskStatus.Completed;
        task.Finding = finding;
        PlanChanged?.Invoke();
        return SerializePlan(message: $"タスク \"{id}\" を完了しました ({CompletedCount}/{_tasks.Count})");
    }

    private string RevisePlan(string argsJson)
    {
        if (!TryParseTasksArg(argsJson, out var newTasks, out var err))
            return ErrorJson(err);

        // 既存タスクの id → status/finding を引き継ぐためのマップ。
        var oldById = _tasks.ToDictionary(t => t.Id, t => t);
        var merged  = new List<PlanTask>(newTasks.Count);
        foreach (var nt in newTasks)
        {
            if (oldById.TryGetValue(nt.Id, out var existing))
            {
                // 同じ id: status / finding を保持しつつ description は新しいもので更新。
                existing.Description = nt.Description;
                merged.Add(existing);
            }
            else
            {
                merged.Add(nt); // 新規: pending のまま
            }
        }
        _tasks.Clear();
        foreach (var t in merged) _tasks.Add(t);
        EstimateRequired = true; // 計画変更後も見積りやり直し
        PlanChanged?.Invoke();
        return SerializePlan(
            message: $"計画を更新しました ({_tasks.Count} タスク, {CompletedCount} 完了)。**次のステップ: estimate_and_confirm を必ず呼んで新 plan のコスト見積りをすること**。");
    }

    /// <summary>AI が立てた plan のコスト見積りを受け取って「heavy / light」を判定し、AI に次のアクションを指示する。
    /// heavy なら AI は次のラウンドで確認文を最終回答として出力する (= ユーザの返答待ちでループ停止)。
    /// light ならそのまま plan 実行に進む。判定後は <see cref="EstimateRequired"/> を false に戻し、
    /// 他ツールのガードを解除する。</summary>
    private string EstimateAndConfirm(string argsJson)
    {
        if (!TryParseObject(argsJson, out var args))
            return ErrorJson("引数 JSON のパースに失敗");

        if (!args.TryGetProperty("estimated_tool_calls", out var ecEl) || !TryGetIntLoose(ecEl, out var estimatedCalls))
            return ErrorJson("estimated_tool_calls (= 残り見込みツール呼び出し回数) が指定されていません");
        var scanBreadth = args.TryGetProperty("scan_breadth", out var sbEl) && sbEl.ValueKind == JsonValueKind.String
            ? (sbEl.GetString() ?? "single") : "single";
        var readsFullThread = args.TryGetProperty("reads_full_thread", out var rfEl) && rfEl.ValueKind == JsonValueKind.True;
        var planSummary = args.TryGetProperty("plan_summary", out var psEl) && psEl.ValueKind == JsonValueKind.String
            ? (psEl.GetString() ?? "") : "";
        var lighterAlt = args.TryGetProperty("lighter_alternative", out var laEl) && laEl.ValueKind == JsonValueKind.String
            ? (laEl.GetString() ?? "") : "";

        // 判定後は他ツールのガードを解除 (= 確認文を出すか実行に進むかは AI 側の応答次第)。
        EstimateRequired = false;
        // 直前 heavy 確認は estimate が再度呼ばれた時点で「処理済み」扱い、いったん降ろす。
        // 後段で heavy 判定が再度出たら下記で立て直す。
        HeavyConfirmationPresented = false;

        // ユーザが直前に「やって」「Bで」「軽量で」等の承諾を返している場合、heavy 判定をスキップして強制 light。
        // 「軽量版を実施」と言ったのに軽量版がまた heavy 判定されて再度確認、という無限ループを防ぐ仕組み。
        if (_userPreApproved)
        {
            _userPreApproved = false; // 1 回使ったら降ろす
            return JsonSerializer.Serialize(new
            {
                severity   = "light",
                verdict    = "proceed",
                estimated_tool_calls = estimatedCalls,
                scan_breadth         = scanBreadth,
                reads_full_thread    = readsFullThread,
                user_pre_approved    = true,
                instruction = "ユーザが直前メッセージで既に作業内容を承諾しています (= 承諾ワード検出)。確認をスキップしてそのまま plan 実行に進んでください。",
            }, JsonOpts);
        }

        // 判定ロジック: いずれか該当で heavy。
        // 閾値:
        //   - estimated_tool_calls >= 12: 中規模 (8-11 回) は許容、12 回以上は重い
        //   - scan_breadth == "all_boards": 全板 scan は明確に重い (= many 単独は heavy にしない)
        //   - reads_full_thread: 1 スレ全部読みは明確に重い
        var heavyByCalls    = estimatedCalls >= 12;
        var heavyByBreadth  = scanBreadth.Equals("all_boards", StringComparison.OrdinalIgnoreCase);
        var heavyByReads    = readsFullThread;
        var heavy           = heavyByCalls || heavyByBreadth || heavyByReads;

        if (!heavy)
        {
            return JsonSerializer.Serialize(new
            {
                severity   = "light",
                verdict    = "proceed",
                estimated_tool_calls = estimatedCalls,
                scan_breadth         = scanBreadth,
                reads_full_thread    = readsFullThread,
                instruction = "軽い〜中規模の作業です。確認は不要、そのまま plan のタスクを実行に進んでください。",
            }, JsonOpts);
        }

        // heavy: 確認文の雛形を返す。AI は次のラウンドで「ツール呼び出しせず確認文を最終回答テキストとして出力」する。
        // 次のユーザ送信時に AiChatViewModel が分類器を起動できるよう、フラグを立てる。
        HeavyConfirmationPresented = true;
        var reasons = new List<string>();
        if (heavyByCalls)   reasons.Add($"ツール呼出 {estimatedCalls} 回 (>= 12)");
        if (heavyByBreadth) reasons.Add($"scan 幅 = {scanBreadth} (= 全板スキャン)");
        if (heavyByReads)   reasons.Add("スレ全体読込予定");

        var confirmationTemplate =
            "ご依頼の作業、以下の規模になりそうです:\n" +
            (string.IsNullOrEmpty(planSummary) ? "" : $"- 予想内容: {planSummary}\n") +
            $"- 規模見積り: ツール呼び出し ~{estimatedCalls} 回, スキャン幅 = {scanBreadth}" +
            (readsFullThread ? ", スレ全体読込あり" : "") + "\n" +
            $"- 重い判定の理由: {string.Join(" / ", reasons)}\n" +
            "\n" +
            "以下から選んでください:\n" +
            $"- (A) このまま実施: {(string.IsNullOrEmpty(planSummary) ? "上記の plan を full 実行" : planSummary)}\n" +
            $"- (B) 軽量版で実施: {(string.IsNullOrEmpty(lighterAlt) ? "(軽量代替を提案してください)" : lighterAlt)}\n" +
            "- (C) 別の方向で再依頼してください";

        return JsonSerializer.Serialize(new
        {
            severity   = "heavy",
            verdict    = "halt_and_confirm",
            estimated_tool_calls = estimatedCalls,
            scan_breadth         = scanBreadth,
            reads_full_thread    = readsFullThread,
            heavy_reasons        = reasons,
            instruction = "**重い作業です。次のラウンドではツール呼び出しせず、以下の confirmation_template の内容を最終回答テキストとしてそのまま出力してエージェントループを止めてください。** ユーザの返答が来てから plan 実行に進みます。",
            confirmation_template = confirmationTemplate,
        }, JsonOpts);
    }

    private static bool TryGetIntLoose(JsonElement el, out int value)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Number:
                return el.TryGetInt32(out value);
            case JsonValueKind.String:
                return int.TryParse(el.GetString(), out value);
            default:
                value = 0;
                return false;
        }
    }

    // ---- helpers ----

    private string SerializePlan(string message)
    {
        var planArr = _tasks.Select(t => new
        {
            id          = t.Id,
            description = t.Description,
            status      = t.Status == PlanTaskStatus.Completed ? "completed" : "pending",
            finding     = t.Finding,
        }).ToArray();

        return JsonSerializer.Serialize(new
        {
            message,
            progress = $"{CompletedCount}/{_tasks.Count}",
            plan     = planArr,
        }, JsonOpts);
    }

    /// <summary>tasks 引数を <see cref="PlanTask"/> 列にパース。id 重複も許容して 1 つ目を採用。
    /// id / description が欠けている要素はエラー。</summary>
    private static bool TryParseTasksArg(string argsJson, out List<PlanTask> result, out string error)
    {
        result = new List<PlanTask>();
        error  = "";
        if (!TryParseObject(argsJson, out var args))
        {
            error = "引数 JSON のパースに失敗";
            return false;
        }
        if (!args.TryGetProperty("tasks", out var tasksEl) || tasksEl.ValueKind != JsonValueKind.Array)
        {
            error = "tasks 配列が指定されていません";
            return false;
        }
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var el in tasksEl.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
            {
                error = "tasks の要素はオブジェクトである必要があります";
                return false;
            }
            var id = el.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? (idEl.GetString() ?? "") : "";
            var desc = el.TryGetProperty("description", out var dEl) && dEl.ValueKind == JsonValueKind.String
                ? (dEl.GetString() ?? "") : "";
            if (id.Length == 0)
            {
                error = "tasks の要素に id が必要です";
                return false;
            }
            if (desc.Length == 0)
            {
                error = $"tasks[id=\"{id}\"] に description が必要です";
                return false;
            }
            if (!seenIds.Add(id))
            {
                error = $"tasks 内で id=\"{id}\" が重複しています";
                return false;
            }
            result.Add(new PlanTask
            {
                Id          = id,
                Description = desc,
                Status      = PlanTaskStatus.Pending,
            });
        }
        if (result.Count == 0)
        {
            error = "tasks が空です。最低 1 タスク必要";
            return false;
        }
        return true;
    }

    private static bool TryParseObject(string json, out JsonElement obj)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            obj = doc.RootElement.Clone();
            return obj.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            obj = default;
            return false;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder       = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private static string ErrorJson(string message)
        => JsonSerializer.Serialize(new { error = message }, JsonOpts);
}
