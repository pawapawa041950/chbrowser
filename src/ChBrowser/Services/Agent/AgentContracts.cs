namespace ChBrowser.Services.Agent;

/// <summary>タスクのスキャン幅 (heavy 判定の記述子)。doc/ai-agent-design.md §4.7 (D12)。</summary>
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
/// <see cref="MaxToolCalls"/> は決定論ハードキャップ (超過で打ち切り → partial)、
/// 他 2 つは heavy 判定の記述子。</summary>
public sealed record Limits(int MaxToolCalls, ScanBreadth ScanBreadth, bool ReadsFullThread)
{
    /// <summary>limits 省略時のデフォルト (= light 確定)。doc §4.7。
    /// 既定 12 (= heavy 閾値 24 の半分。横断検索でも比較的すぐ予算切れしないよう確保)。</summary>
    public static Limits Default { get; } = new(MaxToolCalls: 12, ScanBreadth.Single, ReadsFullThread: false);
}

/// <summary>Strategist → Worker に渡すタスク仕様。doc §2.3 / §4.7 (D12)。</summary>
public sealed record TaskSpec(string Id, string Goal, string ContextHint, Limits Limits);

/// <summary>Worker → Strategist に返すタスク結果。doc §2.3 / §4.7 (D12)。
/// 生出力は含めず finding (要約) と evidenceIds (archive 参照 id) のみ。</summary>
public sealed record TaskResult(
    string Id,
    TaskOutcome Status,
    string Finding,
    IReadOnlyList<string> EvidenceIds,
    int ToolCallsUsed);

/// <summary>L1 ToolRuntime → Worker に返す正規化済みツール出力。doc §2.3 (D5)。
/// 成功時 <see cref="NormalizedResult"/> + <see cref="ArchiveId"/>、失敗時 <see cref="StructuredError"/>。</summary>
public sealed record ToolOutput(bool Ok, string? NormalizedResult, string? StructuredError, string? ArchiveId);
