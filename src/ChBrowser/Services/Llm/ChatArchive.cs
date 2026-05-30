using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChBrowser.Services.Llm;

/// <summary>archive エントリの種類。</summary>
public enum ArchiveKind
{
    /// <summary>ユーザ発言。</summary>
    UserMessage,
    /// <summary>アシスタントの本文 (= &lt;think&gt; を除いた表示テキスト)。</summary>
    AssistantText,
    /// <summary>アシスタントの思考過程 (= &lt;think&gt;...&lt;/think&gt; 部の中身)。</summary>
    AssistantThink,
    /// <summary>ツール呼び出しと結果のペア。</summary>
    ToolCall,
}

/// <summary>archive の 1 エントリ。<see cref="Id"/> は安定 (= チャットセッション内で重複しない)。</summary>
public sealed class ArchiveEntry
{
    public string      Id        { get; init; } = "";
    public ArchiveKind Kind      { get; init; }
    public int         Sequence  { get; init; }
    /// <summary>このエントリが属するタスク id (= 推定可能なら)。</summary>
    public string?     TaskId    { get; init; }
    /// <summary>ToolCall のみ: 呼ばれたツール名。</summary>
    public string?     ToolName  { get; init; }
    /// <summary>ToolCall のみ: 引数 JSON 原文。</summary>
    public string?     ToolArgs  { get; init; }
    /// <summary>本文 (= 復元用の完全な原文)。</summary>
    public string      Content   { get; init; } = "";
}

/// <summary>
/// チャットセッション内のツール結果原文を順序付きで保管し、LLM が後で原文を引き戻せるようにする。
///
/// 新 3 レイヤーエージェントでは、Worker のツール結果 (= 生データ) を <see cref="RecordToolCall"/> で
/// 記録し、上層 (Strategist) には finding と evidence id だけを渡す (D5)。原文が要るときは
/// <c>recall_archive(id)</c> で引き戻す。
/// </summary>
public sealed class ChatArchive
{
    private readonly List<ArchiveEntry> _entries = new();
    private int _seq = 0;

    /// <summary>archive 内の全エントリ (順序保持)。読み取り専用、追加は Record* 経由のみ。</summary>
    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    // ---- 記録系 ----

    public string RecordToolCall(string toolName, string toolArgs, string result, string? taskId)
    {
        var id = NextId();
        _entries.Add(new ArchiveEntry
        {
            Id       = id,
            Kind     = ArchiveKind.ToolCall,
            Sequence = _seq,
            TaskId   = taskId,
            ToolName = toolName,
            ToolArgs = toolArgs,
            Content  = result ?? "",
        });
        return id;
    }

    private string NextId() => $"a{++_seq}";

    // ---- ツール実行 ----

    /// <summary><c>recall_archive(id)</c> を実行して原文を返す。未知ツールは null
    /// (= ディスパッチ側で別経路を試す合図)。</summary>
    public string? TryExecute(string name, string argumentsJson)
    {
        try
        {
            return name switch
            {
                "recall_archive" => RecallArchive(argumentsJson),
                _                => null,
            };
        }
        catch (Exception ex)
        {
            return ErrorJson($"archive ツール実行で例外: {ex.Message}");
        }
    }

    private string RecallArchive(string argsJson)
    {
        if (!TryParseObject(argsJson, out var args))
            return ErrorJson("引数 JSON のパースに失敗");
        if (!args.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
            return ErrorJson("id が指定されていません");
        var id = idEl.GetString() ?? "";
        var entry = _entries.FirstOrDefault(e => e.Id == id);
        if (entry is null) return ErrorJson($"id=\"{id}\" のエントリは見つかりません");

        return JsonSerializer.Serialize(new
        {
            id        = entry.Id,
            kind      = KindToString(entry.Kind),
            sequence  = entry.Sequence,
            task_id   = entry.TaskId,
            tool_name = entry.ToolName,
            tool_args = entry.ToolArgs,
            content   = entry.Content,
        }, JsonOpts);
    }

    // ---- helpers ----

    /// <summary>assistant content を「&lt;think&gt;...&lt;/think&gt; を集めた think 部」と
    /// 「それ以外の本文部」に分解する。複数 &lt;think&gt; があれば連結する。
    /// Worker が LLM 応答から本文だけを取り出すのに使う。</summary>
    public static (string Text, string Think) SplitThink(string content)
    {
        if (string.IsNullOrEmpty(content)) return ("", "");
        if (content.IndexOf("<think>", StringComparison.OrdinalIgnoreCase) < 0) return (content, "");

        var parts = new List<string>();
        foreach (Match m in ThinkBlockRe.Matches(content))
            parts.Add(m.Groups[1].Value);

        var text  = ThinkBlockRe.Replace(content, "");
        text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
        var think = string.Join("\n\n---\n\n", parts).Trim();
        return (text, think);
    }

    private static readonly Regex ThinkBlockRe =
        new(@"<think>(.*?)</think>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static string KindToString(ArchiveKind k) => k switch
    {
        ArchiveKind.UserMessage     => "user_message",
        ArchiveKind.AssistantText   => "assistant_text",
        ArchiveKind.AssistantThink  => "assistant_think",
        ArchiveKind.ToolCall        => "tool_call",
        _                           => "unknown",
    };

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
