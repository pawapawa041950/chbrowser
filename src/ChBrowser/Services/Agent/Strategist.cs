using System.Text;
using System.Text.Json;
using ChBrowser.Models;
using ChBrowser.Services.Llm;

namespace ChBrowser.Services.Agent;

/// <summary>1 タスクを Worker に委譲して結果を得るデリゲート (= NewAgentEngine が Worker を起動して提供する)。</summary>
public delegate Task<TaskResult> WorkerDispatcher(TaskSpec spec, IWorkSection section, CancellationToken ct);

/// <summary>L3 戦略レイヤ。doc/ai-agent-design.md §2 / §4 (D10/D11/D13)。
///
/// <para>tool-call ループで plan を所有し、<c>dispatch_task</c> で Worker を起動 (= 1 ツール呼び = Worker 1 本)、
/// finding を見て次の手を決める。会話 (<see cref="_messages"/>) はターンを跨いで永続 (D8)。
/// 生 tool 出力は持たず finding と evidence id のみ受け取る。</para></summary>
public sealed class Strategist
{
    private readonly LlmClient            _llm;
    private readonly LlmSettings          _settings;
    private readonly IAgentHost           _host;
    private readonly WorkerDispatcher     _dispatch;
    private readonly System.Func<string, string> _recallArchive;
    private readonly bool                 _allowParallel;   // 1 ターンの複数 dispatch_task を同時 in-flight にするか (D7)

    private readonly List<LlmChatMessage> _messages = new();   // 永続会話 (D8)
    private readonly List<PlanItem>       _plan      = new();   // 新エンジン独自の簡易 plan
    private readonly Dictionary<string, int> _dispatchCounts = new(System.StringComparer.Ordinal); // D9 リトライ

    // heavy ゲート (D11/D12 + レビュー②: 累積コミット予算)。
    private int  _committedBudget;   // 既に dispatch したタスクの maxToolCalls 合計 (アクティブ依頼単位)
    private bool _planHeavy;         // 現 plan の宣言が heavy か
    private bool _heavyConfirmed;    // ユーザ確認が取れたか
    private bool _lastTurnAskedUser; // 前ターンが ask_user で終わったか (= 解除条件)

    private const int MaxRounds   = 32;  // Strategist ターンの安全上限
    private const int MaxDispatch = 3;   // 同一 task id の再委譲上限 (D9)
    private const int HeavyToolCallThreshold = 24;  // アクティブ依頼の累積コミット予算がこれ以上で heavy (D11/D12)

    private sealed record PlanItem(string Id, string Goal, string Hint, Limits Limits);

    public Strategist(LlmClient llm, LlmSettings settings, IAgentHost host,
                      WorkerDispatcher dispatch, string systemPrompt, System.Func<string, string> recallArchive,
                      bool allowParallel)
    {
        _llm           = llm;
        _settings      = settings;
        _host          = host;
        _dispatch      = dispatch;
        _recallArchive = recallArchive;
        _allowParallel = allowParallel;
        _messages.Add(new("system", systemPrompt));
    }

    public async Task RunTurnAsync(string userText, CancellationToken ct)
    {
        _host.Begin();

        // heavy ゲート (レビュー②/D11): 前ターンが ask_user で終わっていれば、今回のユーザ返答は確認応答
        // とみなしゲートを開く。そうでなければ (= 新しい依頼) 確認状態をリセットして再度ゲートを要求する。
        // ※ 承認状態はこのターン境界だけで管理する。create_plan では触らない (= 承認後に Strategist が
        //   plan を再宣言しても承認が落ちず、確認ループに陥らないように)。
        _heavyConfirmed    = _lastTurnAskedUser;
        _lastTurnAskedUser = false;

        _messages.Add(new("user", userText));
        var tools = StrategistToolDefs();

        try
        {
            for (var round = 0; round < MaxRounds; round++)
            {
                var checkpoint = _host.WorkCheckpoint();
                var result = await _llm.ChatStreamAsync(_settings, _messages, _host.StreamWork, tools, ct).ConfigureAwait(true);

                // Strategist の LLM 失敗 = 回復不能 → 致命エラー (D15)。
                if (!result.Ok)
                {
                    _host.Error($"AI (Strategist) の呼び出しに失敗しました: {result.Error}");
                    return;
                }

                // テキストのみ = 最終回答。作業エリアに流れた分を巻き戻して本文へ移す (D14)。
                if (result.ToolCalls.Count == 0)
                {
                    _host.RollbackWork(checkpoint);
                    _host.StreamBody(result.Content ?? "");
                    _messages.Add(new("assistant", result.Content ?? ""));
                    _host.End();
                    return;
                }

                // tool_calls ラウンド: assistant メッセージを履歴へ。
                _messages.Add(new("assistant", result.Content ?? "") { ToolCalls = result.ToolCalls });

                // --- 1) 各 tool_call をプリプロセス: 即時系は処理、dispatch_task は準備 (gate/予算/区画) のみ ---
                // (= 並列実行時も gate / 予算 / 区画作成は逐次に確定させ、Worker 実行だけ同時 in-flight にする)
                var results = new Dictionary<string, string>(System.StringComparer.Ordinal);
                var pending = new List<(string Id, TaskSpec Spec, IWorkSection Section)>();
                string? askMessage = null;

                foreach (var tc in result.ToolCalls)
                {
                    if (askMessage is not null)
                    {
                        results[tc.Id] = ErrorJson("ユーザ応答待ちのため未実行");
                        continue;
                    }
                    // 個々のツール処理で例外が出ても会話を壊さない (= 必ず tool 結果を 1 つ返す)。
                    try
                    {
                        switch (tc.Name)
                        {
                            case "create_plan":
                            case "revise_plan":
                                results[tc.Id] = HandlePlan(tc.Name, tc.ArgumentsJson);
                                break;
                            case "recall_archive":
                                results[tc.Id] = _recallArchive(tc.ArgumentsJson ?? "{}");
                                break;
                            case "ask_user":
                                askMessage = ParseStringField(tc.ArgumentsJson, "message") ?? "(確認内容が空です)";
                                results[tc.Id] = "(ユーザに提示し応答待ち)";
                                break;
                            case "dispatch_task":
                            {
                                var (spec, section, error) = PrepareDispatch(tc.ArgumentsJson);
                                if (error is not null) results[tc.Id] = error;
                                else                   pending.Add((tc.Id, spec!, section!));
                                break;
                            }
                            default:
                                results[tc.Id] = ErrorJson($"未知のツール: {tc.Name}");
                                break;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (System.Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[NewAgent] tool '{tc.Name}' 例外: {ex}");
                        results[tc.Id] = ErrorJson($"ツール処理で例外: {ex.Message}");
                    }
                }

                // --- 2) dispatch を実行 ---
                if (pending.Count > 0)
                {
                    if (_allowParallel && pending.Count > 1)
                    {
                        // UI スレッド上の async 並行: 各 Worker の HTTP を同時に in-flight にする
                        // (= バックグラウンドスレッドは使わず、継続/onDelta は UI スレッドに戻る = 安全)。
                        var tasks = new Task<TaskResult>[pending.Count];
                        for (var i = 0; i < pending.Count; i++)
                            tasks[i] = _dispatch(pending[i].Spec, pending[i].Section, ct);
                        var rs = await Task.WhenAll(tasks).ConfigureAwait(true);
                        for (var i = 0; i < pending.Count; i++)
                            results[pending[i].Id] = DispatchResultJson(rs[i]);
                    }
                    else
                    {
                        foreach (var p in pending)
                        {
                            var r = await _dispatch(p.Spec, p.Section, ct).ConfigureAwait(true);
                            results[p.Id] = DispatchResultJson(r);
                        }
                    }
                }

                // --- 3) tool 結果を tool_call 順に履歴へ (= OpenAI 仕様: 各 tool_call に対応する tool 結果が要る) ---
                foreach (var tc in result.ToolCalls)
                    _messages.Add(new("tool", results.TryGetValue(tc.Id, out var rr) ? rr : ErrorJson("結果なし")) { ToolCallId = tc.Id });

                // --- 4) ask_user ならターン終了 (= 次のユーザ送信で再開) ---
                if (askMessage is not null)
                {
                    _host.RollbackWork(checkpoint);
                    _host.StreamBody(askMessage);
                    _host.End();
                    _lastTurnAskedUser = true;
                    return;
                }
            }

            // ラウンド上限。
            _host.Notice("処理が長くなりすぎたため打ち切りました。");
            _host.End();
        }
        catch (OperationCanceledException)
        {
            _host.Notice("中断しました。");
            _host.End();
        }
        catch (System.Exception ex)
        {
            // 致命的例外 (例: ChatStreamAsync が throw する / 想定外) は赤エラーで可視化してターンを閉じる (D15)。
            // ここで End されないと「作業中」のまま固まるため、必ず Error を呼ぶ。
            System.Diagnostics.Debug.WriteLine($"[NewAgent] Strategist 例外: {ex}");
            _host.Error($"エージェント実行中にエラーが発生しました: {ex.Message}");
        }
    }

    // ---- plan ----

    private string HandlePlan(string toolName, string? argsJson)
    {
        var items = ParseTasks(argsJson);
        if (toolName == "create_plan")
        {
            // 新規 plan = plan スコープ状態リセット (D13)。ただし _heavyConfirmed はここでは触らない
            // (= ユーザ確認後に Strategist が plan を再宣言しても承認が失われないように。承認状態は
            //  ターン境界 RunTurnAsync で管理する)。
            _plan.Clear();
            _committedBudget = 0;
            _dispatchCounts.Clear();
        }
        // create / revise とも、与えられたタスクで置換 (B4 は簡易にマージせず置換)。
        if (items.Count > 0)
        {
            _plan.Clear();
            _plan.AddRange(items);
        }

        // heavy 判定 (宣言ベース)。
        var planBudget   = 0;
        var anyAllBoards = false;
        var anyFullThread = false;
        foreach (var p in _plan)
        {
            planBudget    += p.Limits.MaxToolCalls;
            anyAllBoards  |= p.Limits.ScanBreadth == ScanBreadth.AllBoards;
            anyFullThread |= p.Limits.ReadsFullThread;
        }
        _planHeavy = planBudget >= HeavyToolCallThreshold || anyAllBoards || anyFullThread;

        _host.PlanUpdated(BuildPlanView());

        if (_planHeavy && !_heavyConfirmed)
        {
            return JsonOut(new
            {
                severity     = "heavy",
                reason       = $"宣言予算 {planBudget} / all_boards={anyAllBoards} / full_thread={anyFullThread}",
                instruction  = "重い作業です。dispatch_task の前に、必ず ask_user で A=実施 / B=軽量版 / C=別案 を提示してユーザの確認を取ってください。",
                tasks        = _plan.Count,
            });
        }
        return JsonOut(new { severity = "light", instruction = "proceed: dispatch_task を進めてよい。", tasks = _plan.Count });
    }

    // ---- dispatch ----

    /// <summary>dispatch_task の準備 (同期): タスク解決・リトライ上限・heavy ゲート判定・予算予約・区画作成まで。
    /// 成功なら (spec, section, null)、ブロック / エラーなら (null, null, errorJson)。
    /// Worker の実行自体は呼び出し側が行う (= 並列実行のため準備と実行を分離し、gate / 予算は逐次に確定させる)。</summary>
    private (TaskSpec? Spec, IWorkSection? Section, string? Error) PrepareDispatch(string? argsJson)
    {
        var args = ParseObject(argsJson);

        // タスク解決: id が plan にあればそれを使う。無ければ ad-hoc。
        var id   = TryStr(args, "id");
        PlanItem? planItem = (id is not null) ? _plan.Find(p => p.Id == id) : null;

        var goal   = planItem?.Goal ?? TryStr(args, "goal") ?? "";
        var hint   = TryStr(args, "context_hint") ?? planItem?.Hint ?? "";
        var limits = planItem?.Limits ?? ParseLimits(args);
        var taskId = id ?? planItem?.Id ?? $"adhoc{_dispatchCounts.Count + 1}";

        if (string.IsNullOrWhiteSpace(goal))
            return (null, null, ErrorJson("goal が空です (id が plan に無い場合は goal が必須)"));

        // リトライ上限 (D9)。
        _dispatchCounts.TryGetValue(taskId, out var count);
        if (count >= MaxDispatch)
            return (null, null, JsonOut(new { status = "failed", finding = $"タスク {taskId} は再委譲上限 ({MaxDispatch}) に達しました。別アプローチか skip を検討してください。" }));

        // heavy ゲート (累積予算 + plan 宣言)。確認前は dispatch をブロック (D11/レビュー②)。
        var projected = _committedBudget + limits.MaxToolCalls;
        var heavy = _planHeavy || projected >= HeavyToolCallThreshold || limits.ScanBreadth == ScanBreadth.AllBoards || limits.ReadsFullThread;
        if (heavy && !_heavyConfirmed)
            return (null, null, JsonOut(new
            {
                error       = "heavy_gate",
                instruction = "重い作業です。dispatch_task の前に ask_user で A/B/C 確認を取ってください。",
                projected_budget = projected,
            }));

        // 予算を予約 + 区画作成 (= 並列でも一貫させるため Worker 実行の前に確定する)。
        _dispatchCounts[taskId] = count + 1;
        _committedBudget += limits.MaxToolCalls;
        var section = _host.BeginWorkSection(string.IsNullOrEmpty(goal) ? taskId : goal);
        return (new TaskSpec(taskId, goal, hint, limits), section, null);
    }

    private static string DispatchResultJson(TaskResult r) => JsonOut(new
    {
        id              = r.Id,
        status          = r.Status.ToString().ToLowerInvariant(),
        finding         = r.Finding,
        evidence_ids    = r.EvidenceIds,
        tool_calls_used = r.ToolCallsUsed,
    });

    // ---- parsing helpers ----

    private List<PlanItem> ParseTasks(string? argsJson)
    {
        var list = new List<PlanItem>();
        var args = ParseObject(argsJson);
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("tasks", out var tasks) && tasks.ValueKind == JsonValueKind.Array)
        {
            var n = 0;
            foreach (var t in tasks.EnumerateArray())
            {
                n++;
                var id   = TryStr(t, "id") ?? $"t{n}";
                var goal = TryStr(t, "goal") ?? "";
                var hint = TryStr(t, "hint") ?? TryStr(t, "context_hint") ?? "";
                list.Add(new PlanItem(id, goal, hint, ParseLimits(t)));
            }
        }
        return list;
    }

    private static Limits ParseLimits(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return Limits.Default;
        var max = Limits.Default.MaxToolCalls;
        if (el.TryGetProperty("max_tool_calls", out var m) && m.ValueKind == JsonValueKind.Number && m.TryGetInt32(out var mi))
            max = System.Math.Clamp(mi, 1, 128);
        var breadth = ScanBreadth.Single;
        if (el.TryGetProperty("scan_breadth", out var b) && b.ValueKind == JsonValueKind.String)
            breadth = ParseBreadth(b.GetString());
        var full = el.TryGetProperty("reads_full_thread", out var f) && f.ValueKind == JsonValueKind.True;
        return new Limits(max, breadth, full);
    }

    private static ScanBreadth ParseBreadth(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "few"        => ScanBreadth.Few,
        "many"       => ScanBreadth.Many,
        "all_boards" => ScanBreadth.AllBoards,
        _            => ScanBreadth.Single,
    };

    private PlanView BuildPlanView()
    {
        var items = new List<PlanItemView>(_plan.Count);
        foreach (var p in _plan)
            items.Add(new PlanItemView(p.Id, p.Goal, _dispatchCounts.ContainsKey(p.Id)));
        return new PlanView(items);
    }

    private static JsonElement ParseObject(string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return doc.RootElement.Clone();
        }
        catch { return default; }
    }

    private static string? TryStr(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string? ParseStringField(string? json, string name) => TryStr(ParseObject(json), name);

    // ---- tool definitions ----

    private static IReadOnlyList<object> StrategistToolDefs() => new object[]
    {
        Fn("create_plan", "依頼を複数タスクに分割した計画を宣言する。複雑な依頼の最初に呼ぶ。単純な依頼なら呼ばず dispatch_task を直接使ってよい。", new
        {
            type = "object",
            properties = new
            {
                tasks = new
                {
                    type  = "array",
                    description = "タスク配列。",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            id    = new { type = "string", description = "タスク id (任意・短い識別子)。" },
                            goal  = new { type = "string", description = "このタスクのゴール (自然文)。" },
                            hint  = new { type = "string", description = "解くための文脈ヒント (前タスクの finding 抜粋など)。" },
                            max_tool_calls    = new { type = "integer", description = "このタスクのツール予算 (既定 6)。" },
                            scan_breadth      = new { type = "string", description = "single / few / many / all_boards。" },
                            reads_full_thread = new { type = "boolean", description = "スレ全読みか。" },
                        },
                        required = new[] { "goal" },
                    },
                },
            },
            required = new[] { "tasks" },
        }),
        Fn("revise_plan", "計画を作り直す (タスクの追加 / 差し替え)。実行中に方針を変えるときに呼ぶ。", new
        {
            type = "object",
            properties = new
            {
                tasks = new { type = "array", description = "新しいタスク配列 (create_plan と同形式)。", items = new { type = "object" } },
            },
            required = new[] { "tasks" },
        }),
        Fn("dispatch_task", "1 タスクを Worker に委譲して実行し、finding (要約) を受け取る。plan のタスク id を指定するか、id 無しで goal を直接渡す (= 単純依頼の近道)。", new
        {
            type = "object",
            properties = new
            {
                id           = new { type = "string", description = "plan のタスク id (指定時はそのタスクを実行)。" },
                goal         = new { type = "string", description = "id 未指定時のタスクゴール。" },
                context_hint = new { type = "string", description = "Worker に渡す文脈ヒント。" },
                max_tool_calls    = new { type = "integer", description = "ツール予算 (既定 6)。" },
                scan_breadth      = new { type = "string", description = "single / few / many / all_boards。" },
                reads_full_thread = new { type = "boolean", description = "スレ全読みか。" },
            },
            required = System.Array.Empty<string>(),
        }),
        Fn("ask_user", "ユーザに確認 / 質問してターンを終了する。重い作業前の A/B/C 確認や、依頼が曖昧なときに使う。", new
        {
            type = "object",
            properties = new { message = new { type = "string", description = "ユーザに見せる確認 / 質問文。" } },
            required = new[] { "message" },
        }),
        Fn("recall_archive", "finding の evidence id から原文を id 指定で引く (最終回答で逐語引用したいとき)。", new
        {
            type = "object",
            properties = new { id = new { type = "string", description = "エントリ id (= \"aN\")。" } },
            required = new[] { "id" },
        }),
    };

    private static object Fn(string name, string description, object parameters) => new
    {
        type     = "function",
        function = new { name, description, parameters },
    };

    // ---- json out ----

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string JsonOut(object o) => JsonSerializer.Serialize(o, JsonOpts);
    private static string ErrorJson(string message) => JsonSerializer.Serialize(new { error = message }, JsonOpts);
}
