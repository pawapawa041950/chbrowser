using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ChBrowser.Services.Mcp;

/// <summary>MCP サーバが外部に公開するツールを供給する宿主 (= 動作中の ChBrowser 本体)。
///
/// <para>実装 (= <see cref="ChBrowser.ViewModels.MainViewModel"/>) は、公開ツールの定義と実行を提供する。
/// ツールの中身は AI チャットの Worker が使うものと同じ <see cref="ChBrowser.Services.Llm.ThreadToolset"/>
/// (スレ読み取り / 横断 / 開く 系 14 個)。GUI 状態 (今表示中のスレ) や UI 操作 (open_*_in_app) に
/// 依存するため、実行は UI スレッドへマーシャリングする (実装側の責務)。</para></summary>
public interface IMcpToolHost
{
    /// <summary>公開ツールの OpenAI 互換定義
    /// (= <c>{ type:"function", function:{ name, description, parameters } }</c> の配列)。
    /// MCP サーバはこれを MCP の <c>{ name, description, inputSchema }</c> へ変換して提示する。</summary>
    IReadOnlyList<object> GetMcpToolDefinitions();

    /// <summary>ツールを名前で実行し、結果 JSON 文字列を返す。
    /// 実装は UI スレッドへマーシャリングしてから「現在の選択スレ」に束ねた toolset で実行する。
    /// エラーも JSON 文字列 (= <c>{ "error": "..." }</c>) で返す (= 例外を投げない)。</summary>
    Task<string> CallMcpToolAsync(string name, string argumentsJson, CancellationToken ct);
}
