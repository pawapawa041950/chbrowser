using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ChBrowser.Services.Llm;

/// <summary>「LLM / 外部から呼べるツール群」を 1 単位で束ねる公開ツールセットの契約。
///
/// <para>これを実装したツールセットは <see cref="ToolCatalog"/> に登録され、その時点で
/// <b>内蔵エージェント (Worker) と MCP サーバの両方に自動的に公開される</b>。
/// 公開先ごとに別配線を持たないので「MCP に出し忘れる」が構造的に起きない
/// (= カタログが唯一の真実源)。新しいツール群を足すときは <see cref="ToolCatalog.PublicToolsets"/>
/// に 1 行追加するだけでよい。</para>
///
/// <para>シグネチャは既存 <see cref="ThreadToolset"/> に一致させてある (= 既存実装をそのまま昇格できる)。
/// 結果・エラーともに JSON 文字列で返す (= 例外を投げず、エラーは <c>{"error":"..."}</c>)。</para></summary>
public interface IAgentToolset
{
    /// <summary>OpenAI 互換 <c>tools</c> 配列
    /// (= <c>{ type:"function", function:{ name, description, parameters } }</c>)。</summary>
    IReadOnlyList<object> GetToolDefinitions();

    /// <summary>ツールを名前で実行して結果 JSON を返す。
    /// 自分が扱わない名前が来たら <c>{"error":...}</c> を返してよい (= ルーティングは
    /// <see cref="ToolCatalog"/> が定義名で行うので、通常は自分のツールしか来ない)。</summary>
    Task<string> ExecuteAsync(string name, string argumentsJson, CancellationToken ct = default);
}
