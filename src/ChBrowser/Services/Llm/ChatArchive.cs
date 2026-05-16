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
/// チャットセッション内のすべての出来事を順序付きで保管し、LLM が後で原文を引き戻せるようにする。
///
/// 何を archive するか:
/// <list type="bullet">
///   <item>ユーザ発言 (= 自分の指示を後で正確に引きたいとき)</item>
///   <item>アシスタントの本文 (= 過去ラウンドの結論)</item>
///   <item>アシスタントの思考過程 (= 過去の <c>&lt;think&gt;</c>)</item>
///   <item>ツール呼び出し結果 (= 過去の取得データの原文)</item>
/// </list>
///
/// 提供するツール:
/// <list type="bullet">
///   <item><c>list_archive(filter?)</c> — 一覧 (id + kind + 短い brief + 文字数)。検索や絞り込み付き。</item>
///   <item><c>recall_archive(id)</c> — 1 エントリの完全な原文を返す。</item>
/// </list>
///
/// 想定運用: 通常のラウンドではプロンプトを軽量に保ち、必要なときだけ LLM が
/// <c>list_archive</c> → <c>recall_archive</c> でピンポイントに原文を引く。
/// 「省略済」プレースホルダ (= 完了済タスクの tool 結果差し替え) には対応する archive id を埋めるので、
/// LLM はそこから recall できる。
/// </summary>
public sealed class ChatArchive
{
    private readonly List<ArchiveEntry> _entries = new();
    private int _seq = 0;

    /// <summary>list / recall のデフォルト / 上限値。</summary>
    private const int DefaultListLimit = 50;
    private const int MaxListLimit     = 200;
    /// <summary>list_archive で返す brief の最大文字数。</summary>
    private const int BriefMaxChars = 80;

    /// <summary>archive 内の全エントリ (順序保持)。読み取り専用、追加は Record* 経由のみ。</summary>
    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    // ---- 記録系 ----

    public string RecordUserMessage(string text)
    {
        var id = NextId();
        _entries.Add(new ArchiveEntry { Id = id, Kind = ArchiveKind.UserMessage, Sequence = _seq, Content = text ?? "" });
        return id;
    }

    /// <summary>アシスタントの 1 ラウンドの content を think 部と本文部に分解して、それぞれを別エントリとして
    /// 記録する。両方 null/空のときは何も追加しない。返り値は (本文 id, think id) で、空なら null。</summary>
    public (string? TextId, string? ThinkId) RecordAssistantContent(string content, string? taskId)
    {
        var (text, think) = SplitThink(content ?? "");
        string? textId  = null;
        string? thinkId = null;

        if (!string.IsNullOrWhiteSpace(text))
        {
            textId = NextId();
            _entries.Add(new ArchiveEntry { Id = textId, Kind = ArchiveKind.AssistantText,  Sequence = _seq, TaskId = taskId, Content = text });
        }
        if (!string.IsNullOrWhiteSpace(think))
        {
            thinkId = NextId();
            _entries.Add(new ArchiveEntry { Id = thinkId, Kind = ArchiveKind.AssistantThink, Sequence = _seq, TaskId = taskId, Content = think });
        }
        return (textId, thinkId);
    }

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

    // ---- ツール定義 / 実行 ----

    /// <summary>OpenAI 互換 <c>tools</c> 配列。<see cref="ThreadToolset.GetToolDefinitions"/> /
    /// <see cref="PlanToolset.GetToolDefinitions"/> と合わせて LLM に提示する。</summary>
    public IReadOnlyList<object> GetToolDefinitions()
    {
        return new object[]
        {
            new
            {
                type     = "function",
                function = new
                {
                    name        = "list_archive",
                    description = "セッション内で発生した過去の出来事 (ユーザ発言 / アシスタント本文 / 思考過程 / ツール結果) の目録を返す。各エントリは安定 id を持ち、recall_archive で原文を取得できる。必要最小限の filter を指定して、なるべく絞り込むこと。",
                    parameters  = new
                    {
                        type       = "object",
                        properties = new
                        {
                            kind = new
                            {
                                type        = "string",
                                description = "種類で絞る。\"user_message\" / \"assistant_text\" / \"assistant_think\" / \"tool_call\" のいずれか。省略で全種類。",
                            },
                            task_id = new { type = "string", description = "属するタスク id で絞る (= タスク完了時に紐付けられたエントリのみ)。" },
                            keyword = new { type = "string", description = "本文に含まれる部分文字列で絞る (= 大小文字無視)。" },
                            tool_name = new { type = "string", description = "ToolCall 種別のみ: ツール名で絞る。" },
                            limit   = new { type = "integer", description = $"返す件数の上限 (既定 {DefaultListLimit}, 上限 {MaxListLimit})。" },
                        },
                        required = Array.Empty<string>(),
                    },
                },
            },
            new
            {
                type     = "function",
                function = new
                {
                    name        = "recall_archive",
                    description = "指定 id のエントリの完全な原文を返す。長文を引き戻すのでコンテキストを食う点に注意 — 引用や厳密な確認が要るときに限って使うこと。",
                    parameters  = new
                    {
                        type       = "object",
                        properties = new
                        {
                            id = new { type = "string", description = "list_archive で得たエントリ id (= \"aN\" 形式)。" },
                        },
                        required = new[] { "id" },
                    },
                },
            },
        };
    }

    public string? TryExecute(string name, string argumentsJson)
    {
        try
        {
            return name switch
            {
                "list_archive"   => ListArchive(argumentsJson),
                "recall_archive" => RecallArchive(argumentsJson),
                _                => null,
            };
        }
        catch (Exception ex)
        {
            return ErrorJson($"archive ツール実行で例外: {ex.Message}");
        }
    }

    private string ListArchive(string argsJson)
    {
        // argsJson は空オブジェクト ({}) でも明示パースを通すので、ここでは寛容に。
        ArchiveKind? kindFilter = null;
        string?      taskFilter = null;
        string?      kwFilter   = null;
        string?      toolFilter = null;
        var          limit      = DefaultListLimit;

        if (TryParseObject(argsJson, out var args))
        {
            if (args.TryGetProperty("kind", out var kEl) && kEl.ValueKind == JsonValueKind.String)
                kindFilter = ParseKindString(kEl.GetString());
            if (args.TryGetProperty("task_id", out var tEl) && tEl.ValueKind == JsonValueKind.String)
                taskFilter = tEl.GetString();
            if (args.TryGetProperty("keyword", out var wEl) && wEl.ValueKind == JsonValueKind.String)
                kwFilter = wEl.GetString();
            if (args.TryGetProperty("tool_name", out var nEl) && nEl.ValueKind == JsonValueKind.String)
                toolFilter = nEl.GetString();
            if (args.TryGetProperty("limit", out var lEl) && TryGetIntLoose(lEl, out var lim))
                limit = Math.Clamp(lim, 1, MaxListLimit);
        }

        IEnumerable<ArchiveEntry> seq = _entries;
        if (kindFilter is { } k)      seq = seq.Where(e => e.Kind == k);
        if (!string.IsNullOrEmpty(taskFilter)) seq = seq.Where(e => string.Equals(e.TaskId, taskFilter, StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(toolFilter)) seq = seq.Where(e => string.Equals(e.ToolName, toolFilter, StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(kwFilter))
        {
            // OrdinalIgnoreCase で本文 / 引数 / ツール名のいずれかに含まれていれば該当
            var kw = kwFilter!;
            seq = seq.Where(e =>
                (e.Content   != null && e.Content.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) ||
                (e.ToolArgs  != null && e.ToolArgs.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) ||
                (e.ToolName  != null && e.ToolName.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0));
        }

        var list = seq.ToList();
        var total = list.Count;
        // 新しい順に並べる (= 最近の方が思い出したい確率が高い前提)。
        list.Reverse();
        var truncated = list.Count > limit;
        if (truncated) list = list.Take(limit).ToList();

        var entries = list.Select(e => new
        {
            id        = e.Id,
            kind      = KindToString(e.Kind),
            sequence  = e.Sequence,
            task_id   = e.TaskId,
            tool_name = e.ToolName,
            tool_args = e.ToolArgs,
            brief     = BuildBrief(e),
            length    = e.Content.Length,
        }).ToArray();

        return JsonSerializer.Serialize(new
        {
            total_matched = total,
            returned      = entries.Length,
            truncated,
            entries,
        }, JsonOpts);
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
    /// 「それ以外の本文部」に分解する。複数 &lt;think&gt; があれば連結する。</summary>
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

    private static string BuildBrief(ArchiveEntry e)
    {
        switch (e.Kind)
        {
            case ArchiveKind.ToolCall:
                var args = e.ToolArgs ?? "";
                if (args.Length > 60) args = args.Substring(0, 60) + "…";
                return $"{e.ToolName}({args})";
            default:
                var c = e.Content ?? "";
                c = Regex.Replace(c, @"\s+", " ").Trim();
                if (c.Length > BriefMaxChars) c = c.Substring(0, BriefMaxChars) + "…";
                return c;
        }
    }

    private static string KindToString(ArchiveKind k) => k switch
    {
        ArchiveKind.UserMessage     => "user_message",
        ArchiveKind.AssistantText   => "assistant_text",
        ArchiveKind.AssistantThink  => "assistant_think",
        ArchiveKind.ToolCall        => "tool_call",
        _                           => "unknown",
    };

    private static ArchiveKind? ParseKindString(string? s) => s switch
    {
        "user_message"     => ArchiveKind.UserMessage,
        "assistant_text"   => ArchiveKind.AssistantText,
        "assistant_think"  => ArchiveKind.AssistantThink,
        "tool_call"        => ArchiveKind.ToolCall,
        _                  => null,
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

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder       = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private static string ErrorJson(string message)
        => JsonSerializer.Serialize(new { error = message }, JsonOpts);
}
