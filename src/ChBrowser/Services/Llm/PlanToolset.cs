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
                "create_plan"   => CreatePlan(argumentsJson),
                "complete_task" => CompleteTask(argumentsJson),
                "revise_plan"   => RevisePlan(argumentsJson),
                _               => null,
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
        PlanChanged?.Invoke();
        return SerializePlan(message: $"計画を作成しました ({_tasks.Count} タスク)");
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
        PlanChanged?.Invoke();
        return SerializePlan(message: $"計画を更新しました ({_tasks.Count} タスク, {CompletedCount} 完了)");
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
