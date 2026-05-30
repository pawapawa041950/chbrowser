using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ChBrowser.Services.Llm;

/// <summary>公開ツール (= 内蔵エージェントと MCP サーバの双方に出すツール群) の<b>唯一の真実源</b>。
///
/// <para>内蔵エージェント (<c>ToolRuntime</c>) も MCP サーバ (<c>MainViewModel</c> 経由) も、
/// ツール定義と実行をここから導出する。よって「エージェントには出るが MCP には出ない」状態を
/// 作れない (= MCP 公開が構造的に必須・実装忘れが起きない)。
/// 新しいツール群を足すときは <see cref="PublicToolsets"/> に 1 行追加するだけでよい。</para>
///
/// <para>ルーティングは各ツールセットの定義名 (= <c>function.name</c>) で行う。
/// 名前→ツールセットの対応は定義から導出する (= ツールセット内で名前を二重管理しない)。</para></summary>
public static class ToolCatalog
{
    /// <summary>公開ツールセット群を構築する<b>唯一の登録ポイント</b>。
    /// ここに並べたツールセットがそのままエージェント / MCP の公開ツール表面になる。
    ///
    /// <param name="thread">スレ読み取り・横断・開く 系 (= attached / 現在スレに束ねた toolset)。null 可。</param>
    /// 将来ツール群を増やす場合: 引数を足し、下のリストに <c>list.Add(...)</c> するだけ
    /// (= エージェントと MCP の両方へ自動反映)。</summary>
    public static IReadOnlyList<IAgentToolset> PublicToolsets(ThreadToolset? thread)
    {
        var list = new List<IAgentToolset>();
        if (thread is not null) list.Add(thread);
        // 将来の公開ツール群はここに追加する (例: list.Add(favoritesToolset);)。
        return list;
    }

    /// <summary>ツールセット群の OpenAI 互換定義を連結して返す。</summary>
    public static IReadOnlyList<object> Definitions(IReadOnlyList<IAgentToolset> toolsets)
    {
        var all = new List<object>();
        foreach (var ts in toolsets) all.AddRange(ts.GetToolDefinitions());
        return all;
    }

    /// <summary><paramref name="name"/> を扱うツールセットを定義名で探して実行する。
    /// どのツールセットも扱わなければ null (= 呼び出し側で「未知のツール」として処理)。</summary>
    public static async Task<string?> TryExecuteAsync(
        IReadOnlyList<IAgentToolset> toolsets, string name, string argumentsJson, CancellationToken ct)
    {
        foreach (var ts in toolsets)
        {
            if (ToolNames(ts).Contains(name))
                return await ts.ExecuteAsync(name, argumentsJson, ct).ConfigureAwait(true);
        }
        return null;
    }

    // ---- 名前抽出 (定義からの単一導出 + インスタンス単位キャッシュ) ----

    private static readonly ConditionalWeakTable<IAgentToolset, HashSet<string>> _nameCache = new();

    private static HashSet<string> ToolNames(IAgentToolset ts)
    {
        if (_nameCache.TryGetValue(ts, out var cached)) return cached;
        var set = ExtractNames(ts.GetToolDefinitions());
        _nameCache.Add(ts, set);
        return set;
    }

    /// <summary>OpenAI 形式定義配列から <c>function.name</c> を全部集める。</summary>
    private static HashSet<string> ExtractNames(IReadOnlyList<object> defs)
    {
        var set = new HashSet<string>(System.StringComparer.Ordinal);
        try
        {
            var raw = JsonSerializer.Serialize(defs, JsonOpts);
            using var doc = JsonDocument.Parse(raw);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object
                    && fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                {
                    var name = n.GetString();
                    if (!string.IsNullOrEmpty(name)) set.Add(name);
                }
            }
        }
        catch { /* 形が壊れていれば空集合 (= 何も扱わない) */ }
        return set;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
