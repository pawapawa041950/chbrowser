namespace ChBrowser.Services.Agent;

/// <summary>タスクのスキャン幅 (予算と heavy 判定の記述子)。doc/ai-agent-design.md §4.7 (D12)。</summary>
public enum ScanBreadth
{
    Single,     // 単一板 / 単一スレ内
    Few,        // 2-4 板
    Many,       // 5+ 板
    AllBoards,  // 全板スキャン (heavy トリガ)
}

/// <summary>タスク完了状態 (TaskResult.status)。doc §4.4 (D9)。
/// ※ BCL の <see cref="System.Threading.Tasks.TaskStatus"/> との衝突を避けるため別名。</summary>
public enum TaskOutcome
{
    Done,     // ゴール達成
    Partial,  // 予算 / ステップ上限で打ち切り
    Failed,   // 達成不能
}

/// <summary>Worker のタスク予算・記述子。doc §4.7 (D12)。
/// <see cref="MaxToolCalls"/> は決定論ハードキャップ (超過で打ち切り → まとめさせて partial)、
/// 他 2 つは heavy 判定の記述子。予算はモデルに計算させず、<see cref="For"/> でスキャン幅から決める。</summary>
public sealed record Limits(int MaxToolCalls, ScanBreadth ScanBreadth, bool ReadsFullThread)
{
    /// <summary>limits 省略時のデフォルト (単一スレ / 単一板)。</summary>
    public static Limits Default { get; } = For(ScanBreadth.Single, readsFullThread: false);

    /// <summary>スキャン幅ごとの既定予算 (ツール呼び出し回数)。横断は板ごとに list_threads + 確認 + open が要る見積もり。</summary>
    public static int BudgetFor(ScanBreadth breadth, bool readsFullThread)
    {
        var n = breadth switch
        {
            ScanBreadth.Few       => 40,
            ScanBreadth.Many      => 64,
            ScanBreadth.AllBoards => 128,
            _                     => 24,
        };
        // スレ全読み: get_posts は 1 回 50 件なので 1000 レスで 20 回 + 余裕
        return readsFullThread ? System.Math.Max(n, 40) : n;
    }

    public static Limits For(ScanBreadth breadth, bool readsFullThread)
        => new(BudgetFor(breadth, readsFullThread), breadth, readsFullThread);
}

/// <summary>Strategist → Worker に渡すタスク仕様。doc §2.3 / §4.7 (D12)。
/// <see cref="UserRequest"/> はユーザの依頼の原文 (Worker が goal の意図を取り違えないための参考)。</summary>
public sealed record TaskSpec(string Id, string Goal, string ContextHint, Limits Limits, string UserRequest = "");

/// <summary>Worker → Strategist に返すタスク結果。doc §2.3 / §4.7 (D12)。
/// 生出力は含めず finding (報告) と evidenceIds (archive 参照 id) のみ。</summary>
public sealed record TaskResult(
    string Id,
    TaskOutcome Status,
    string Finding,
    IReadOnlyList<string> EvidenceIds,
    int ToolCallsUsed);

/// <summary>L1 ToolRuntime → Worker に返す正規化済みツール出力。doc §2.3 (D5)。
/// 成功時 <see cref="NormalizedResult"/> + <see cref="ArchiveId"/>、失敗時 <see cref="StructuredError"/>。</summary>
public sealed record ToolOutput(bool Ok, string? NormalizedResult, string? StructuredError, string? ArchiveId);
