using System.Text.Json;
using ChBrowser.Services.Llm;

namespace ChBrowser.Services.Agent;

/// <summary>L1 ツール実行ランタイム (LLM なし・純粋コード)。doc/ai-agent-design.md §2.2 / §4.5 (D1 / D5)。
///
/// <para>Worker からの 1 ツール呼び出しを <b>検証・実行・正規化・archive 記録 (id 返却)</b> して
/// <see cref="ToolOutput"/> を返す。estimate ゲートは持たない (= heavy 判定は L3 ポリシー)。
/// 生の tool 出力は archive にだけ置き、Worker へは正規化結果 + archive id を返す
/// (= 生出力が Worker のスコープを超えない / D5)。</para>
///
/// <para>提示するツール = スレ読み取り / 横断 / 開く 系 + <c>recall_archive</c> (id 指定)。
/// <c>list_archive</c> は <b>Worker には渡さない</b> (= session 全体の探索を封じ、タスク隔離 D2 を守る / レビュー①)。</para>
///
/// <para>※ B5 の並列実行は「UI スレッド上の async 並行 (複数 HTTP を同時 in-flight にするがスレッドは増やさない)」
/// 方式を採ったため、archive 記録も含め新エンジンの全アクセスが UI スレッドで直列化される (= ロック不要)。
/// もし将来バックグラウンドスレッドで並列化する設計に変える場合は id 採番のロックが要る。</para></summary>
public sealed class ToolRuntime
{
    private readonly AgentToolContext _ctx;
    private readonly ChatArchive      _archive;

    public ToolRuntime(AgentToolContext ctx)
    {
        _ctx     = ctx;
        _archive = ctx.Archive;
    }

    /// <summary>現在のスレ toolset (= context 切替で差し替わるので毎回 ctx から読む)。</summary>
    private ThreadToolset? Thread => _ctx.Thread;

    /// <summary>Worker に提示するツール定義。スレ系 + <c>recall_archive</c> (id 指定)。<c>list_archive</c> は含めない。</summary>
    public IReadOnlyList<object> GetWorkerToolDefinitions()
    {
        var defs = new List<object>();
        if (Thread is not null) defs.AddRange(Thread.GetToolDefinitions());
        defs.Add(RecallArchiveToolDef());
        return defs;
    }

    /// <summary>1 ツール呼び出しを実行する。
    /// 結果 JSON に top-level <c>"error"</c> があれば失敗 (= structuredError) とみなす。
    /// 成功かつ生データ系ツール (= スレ読み取り) なら archive に記録し <see cref="ToolOutput.ArchiveId"/> を付ける。
    /// <paramref name="taskId"/> は archive エントリの紐付け用 (= 後で finding の evidence として引ける)。</summary>
    public async Task<ToolOutput> ExecuteAsync(string name, string argumentsJson, string? taskId, CancellationToken ct)
    {
        var args = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson;

        // list_archive は Worker に渡さない (隔離)。呼ばれたら構造化エラーで弾く。
        if (name == "list_archive")
            return Fail("list_archive は Worker では使用できません (Strategist から渡された id を recall_archive で引いてください)");

        string raw;
        bool   archivable;   // 生データ系か (= archive に記録するか)
        try
        {
            if (name == "recall_archive")
            {
                raw        = _archive.TryExecute(name, args) ?? ErrorJson("recall_archive 実行に失敗");
                archivable = false;  // 既存 archive の引き戻し → 再記録しない
            }
            else if (Thread is not null)
            {
                raw        = await Thread.ExecuteAsync(name, args, ct).ConfigureAwait(true);
                archivable = true;   // スレ読み取り結果 = 原文 → archive 対象
            }
            else
            {
                return Fail($"ツール \"{name}\" はこのチャットでは利用できません");
            }
        }
        catch (OperationCanceledException)
        {
            throw;  // キャンセルは握り潰さず上位 (Worker / engine) へ
        }
        catch (System.Exception ex)
        {
            return Fail($"ツール実行で例外: {ex.Message}");
        }

        // 正規化 / エラー判定: 結果 JSON に top-level "error" があれば失敗。元の error JSON を構造化エラーとして返す。
        if (IsErrorJson(raw))
            return new ToolOutput(Ok: false, NormalizedResult: null, StructuredError: raw, ArchiveId: null);

        // 成功。生データ系なら archive に記録して id を付ける。
        string? archiveId = archivable ? _archive.RecordToolCall(name, args, raw, taskId) : null;
        return new ToolOutput(Ok: true, NormalizedResult: raw, StructuredError: null, ArchiveId: archiveId);
    }

    // ---- helpers ----

    private static ToolOutput Fail(string message)
        => new(Ok: false, NormalizedResult: null, StructuredError: ErrorJson(message), ArchiveId: null);

    /// <summary>結果文字列が <c>{ "error": "..." }</c> 形式かどうか。JSON でない通常結果は false (= 正常)。</summary>
    private static bool IsErrorJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var e)
                && e.ValueKind == JsonValueKind.String;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>recall_archive のツール定義 (= <see cref="ChatArchive.GetToolDefinitions"/> のうち list_archive を除いた片方)。</summary>
    private static object RecallArchiveToolDef() => new
    {
        type     = "function",
        function = new
        {
            name        = "recall_archive",
            description = "指定 id のエントリの完全な原文を返す。Strategist から渡された evidence id を厳密に確認 / 引用したいときに使う。長文を引き戻すので必要なときだけ。",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    id = new { type = "string", description = "エントリ id (= \"aN\" 形式)。" },
                },
                required = new[] { "id" },
            },
        },
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string ErrorJson(string message)
        => JsonSerializer.Serialize(new { error = message }, JsonOpts);
}
