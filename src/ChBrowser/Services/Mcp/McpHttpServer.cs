using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ChBrowser.Services.Mcp;

/// <summary>
/// ChBrowser 内蔵ツール (スレ読み取り / 横断 / 開く 系 14 個) を MCP (Model Context Protocol) で
/// 外部の MCP クライアント (Claude Desktop / Cursor 等) に公開する localhost HTTP サーバ。
///
/// <para><b>トランスポート</b>: MCP の Streamable HTTP を最小実装する。動作中の本体プロセス内で
/// <see cref="TcpListener"/> を 127.0.0.1 にバインドし (= 非管理者でも URL ACL 不要)、自前で
/// HTTP/1.1 のリクエスト/レスポンスを処理する。POST で受けた JSON-RPC を処理して
/// <c>application/json</c> で 1 応答を返す (サーバ起点の SSE は持たないので GET は 405)。</para>
///
/// <para><b>対応メソッド</b>: <c>initialize</c> / <c>notifications/initialized</c> /
/// <c>tools/list</c> / <c>tools/call</c> / <c>ping</c>。</para>
///
/// <para><b>セキュリティ</b>: loopback のみにバインドし外部からは到達不能。さらに DNS リバインディング
/// 対策として、ブラウザ由来の cross-origin (= localhost 以外の <c>Origin</c> ヘッダ) は 403 で拒否する
/// (ネイティブ MCP クライアントは通常 Origin を送らないので許可される)。</para>
/// </summary>
public sealed class McpHttpServer : IDisposable
{
    private readonly IMcpToolHost _host;
    private readonly int          _port;

    private TcpListener?             _listener;
    private CancellationTokenSource? _cts;
    private Task?                    _acceptLoop;

    private static readonly string ServerVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

    /// <summary>サーバが現在 listen しているか。</summary>
    public bool IsRunning => _listener is not null;

    /// <summary>現在の待ち受けポート。</summary>
    public int Port => _port;

    public McpHttpServer(IMcpToolHost host, int port)
    {
        _host = host;
        _port = port;
    }

    /// <summary>サーバを起動する。既に起動済みなら何もしない。
    /// バインドに失敗 (ポート使用中等) した場合は例外を投げず false を返す。</summary>
    public bool Start()
    {
        if (_listener is not null) return true;
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, _port);
            listener.Start();
            _listener  = listener;
            _cts       = new CancellationTokenSource();
            _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
            Debug.WriteLine($"[MCP] サーバ起動: http://127.0.0.1:{_port}/mcp");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MCP] 起動失敗 (port={_port}): {ex.Message}");
            _listener = null;
            _cts?.Dispose();
            _cts = null;
            return false;
        }
    }

    /// <summary>サーバを停止する。</summary>
    public void Stop()
    {
        if (_listener is null) return;
        try { _cts?.Cancel(); } catch { /* noop */ }
        try { _listener.Stop(); } catch { /* noop */ }
        _listener = null;
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* noop */ }
        _acceptLoop = null;
        _cts?.Dispose();
        _cts = null;
        Debug.WriteLine("[MCP] サーバ停止");
    }

    public void Dispose() => Stop();

    // ---- accept ループ ----

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException)     { break; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MCP] accept 失敗: {ex.Message}");
                break;
            }
            // 各接続を独立に処理 (= 接続ごとに 1 リクエスト・Connection: close)。
            _ = Task.Run(() => HandleClientAsync(client, ct));
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 15000;
                client.SendTimeout    = 15000;
                using var stream = client.GetStream();

                var req = ReadRequest(stream);
                if (req is null) return;

                // CORS / DNS リバインディング対策: localhost 以外の Origin は拒否。
                if (!IsOriginAllowed(req.Origin))
                {
                    WriteResponse(stream, 403, "Forbidden", "application/json",
                        "{\"error\":\"origin not allowed\"}", req.Origin);
                    return;
                }

                switch (req.Method)
                {
                    case "OPTIONS":
                        // CORS プリフライト。
                        WriteResponse(stream, 204, "No Content", null, "", req.Origin);
                        return;
                    case "GET":
                        // サーバ起点 SSE は提供しない (= Streamable HTTP 仕様上 405 可)。
                        WriteResponse(stream, 405, "Method Not Allowed", "application/json",
                            "{\"error\":\"this MCP endpoint does not provide an SSE stream; use POST\"}", req.Origin);
                        return;
                    case "POST":
                        var responseJson = await HandleRpcAsync(req.Body, ct).ConfigureAwait(false);
                        if (responseJson is null)
                        {
                            // 通知 (id 無し) → 本文無しで 202。
                            WriteResponse(stream, 202, "Accepted", null, "", req.Origin);
                        }
                        else
                        {
                            WriteResponse(stream, 200, "OK", "application/json", responseJson, req.Origin);
                        }
                        return;
                    default:
                        WriteResponse(stream, 405, "Method Not Allowed", "application/json",
                            "{\"error\":\"unsupported method\"}", req.Origin);
                        return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MCP] 接続処理で例外: {ex.Message}");
            }
        }
    }

    // ---- JSON-RPC ディスパッチ ----

    /// <summary>1 つの JSON-RPC リクエスト本文を処理して応答 JSON を返す。
    /// 通知 (id 無し) は null を返す (= 本文無し 202)。</summary>
    private async Task<string?> HandleRpcAsync(string body, CancellationToken ct)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            root = doc.RootElement.Clone();
        }
        catch
        {
            return RpcError(null, -32700, "Parse error");
        }

        // バッチ (配列) は最小実装では非対応。
        if (root.ValueKind != JsonValueKind.Object)
            return RpcError(null, -32600, "Invalid Request");

        var method = root.TryGetProperty("method", out var mEl) && mEl.ValueKind == JsonValueKind.String
            ? mEl.GetString() ?? "" : "";
        var hasId  = root.TryGetProperty("id", out var idEl) &&
                     idEl.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
        var idJson = hasId ? idEl.GetRawText() : null;
        var prms   = root.TryGetProperty("params", out var pEl) ? pEl : default;

        // 通知 (id 無し): 応答しない。
        if (!hasId)
        {
            // notifications/initialized 等は黙って受理。
            return null;
        }

        switch (method)
        {
            case "initialize":
                return RpcResult(idJson!, BuildInitializeResult(prms));
            case "ping":
                return RpcResult(idJson!, "{}");
            case "tools/list":
                return RpcResult(idJson!, BuildToolsListResult());
            case "tools/call":
                return RpcResult(idJson!, await BuildToolsCallResultAsync(prms, ct).ConfigureAwait(false));
            default:
                return RpcError(idJson, -32601, $"Method not found: {method}");
        }
    }

    private static string BuildInitializeResult(JsonElement prms)
    {
        // クライアントが要求した protocolVersion をそのまま返す (= サポート表明)。無ければ既定。
        var protocolVersion = "2025-06-18";
        if (prms.ValueKind == JsonValueKind.Object &&
            prms.TryGetProperty("protocolVersion", out var pv) && pv.ValueKind == JsonValueKind.String)
        {
            protocolVersion = pv.GetString() ?? protocolVersion;
        }

        var result = new
        {
            protocolVersion,
            capabilities = new
            {
                tools = new { listChanged = false },
            },
            serverInfo = new
            {
                name    = "ChBrowser",
                version = ServerVersion,
            },
            instructions = "ChBrowser (5ch 専用ブラウザ) のスレッド読み取り・板/スレ横断・アプリ操作ツール群です。" +
                           "thread_url 省略時は ChBrowser で現在表示中のスレが対象になります。",
        };
        return JsonSerializer.Serialize(result, JsonOpts);
    }

    /// <summary>tools/list の result。既存 OpenAI 形式定義を MCP の {name, description, inputSchema} へ変換。</summary>
    private string BuildToolsListResult()
    {
        var defs = _host.GetMcpToolDefinitions();
        var tools = new List<object>(defs.Count);

        // 既存定義を JSON 化して function.{name,description,parameters} を読み出す。
        var raw = JsonSerializer.Serialize(defs, JsonOpts);
        using var doc = JsonDocument.Parse(raw);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (!el.TryGetProperty("function", out var fn) || fn.ValueKind != JsonValueKind.Object) continue;
            var name = fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            if (string.IsNullOrEmpty(name)) continue;
            var desc = fn.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";

            // inputSchema = parameters (JSON Schema)。無ければ空オブジェクトスキーマ。
            object inputSchema;
            if (fn.TryGetProperty("parameters", out var pp) && pp.ValueKind == JsonValueKind.Object)
                inputSchema = JsonSerializer.Deserialize<JsonElement>(pp.GetRawText());
            else
                inputSchema = new { type = "object", properties = new { } };

            tools.Add(new { name, description = desc, inputSchema });
        }

        return JsonSerializer.Serialize(new { tools }, JsonOpts);
    }

    /// <summary>tools/call の result。ツールを実行し content[] に結果 JSON テキストを 1 件入れて返す。
    /// 結果が <c>{"error":...}</c> 形式なら isError=true。</summary>
    private async Task<string> BuildToolsCallResultAsync(JsonElement prms, CancellationToken ct)
    {
        var name = prms.ValueKind == JsonValueKind.Object &&
                   prms.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String
            ? nEl.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(name))
            return ToolCallResultJson("{\"error\":\"tools/call: name が指定されていません\"}", isError: true);

        // arguments はオブジェクト。JSON 文字列にして既存 toolset へ渡す。
        var argsJson = "{}";
        if (prms.TryGetProperty("arguments", out var aEl) && aEl.ValueKind == JsonValueKind.Object)
            argsJson = aEl.GetRawText();

        string toolResult;
        try
        {
            toolResult = await _host.CallMcpToolAsync(name, argsJson, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            toolResult = JsonSerializer.Serialize(new { error = $"ツール実行で例外: {ex.Message}" }, JsonOpts);
        }

        return ToolCallResultJson(toolResult, isError: IsErrorJson(toolResult));
    }

    private static string ToolCallResultJson(string text, bool isError)
    {
        var result = new
        {
            content = new object[] { new { type = "text", text } },
            isError,
        };
        return JsonSerializer.Serialize(result, JsonOpts);
    }

    private static bool IsErrorJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var e)
                && e.ValueKind == JsonValueKind.String;
        }
        catch { return false; }
    }

    // ---- JSON-RPC 包み ----

    /// <summary>result を JSON-RPC 応答に包む。<paramref name="resultJson"/> は生 JSON 文字列。</summary>
    private static string RpcResult(string idJson, string resultJson)
        => $"{{\"jsonrpc\":\"2.0\",\"id\":{idJson},\"result\":{resultJson}}}";

    private static string RpcError(string? idJson, int code, string message)
    {
        var id = idJson ?? "null";
        var msg = JsonSerializer.Serialize(message);
        return $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"error\":{{\"code\":{code},\"message\":{msg}}}}}";
    }

    // ---- HTTP 最小実装 ----

    private sealed record HttpRequest(string Method, string Path, string Body, string? Origin);

    /// <summary>HTTP/1.1 リクエストを読む。ヘッダを CRLFCRLF まで読み、Content-Length 分の本文を読む。
    /// 形式不正 / 切断時は null。</summary>
    private static HttpRequest? ReadRequest(NetworkStream stream)
    {
        var headerBytes = new List<byte>(1024);
        int b;
        // \r\n\r\n まで読む。
        while ((b = stream.ReadByte()) != -1)
        {
            headerBytes.Add((byte)b);
            var c = headerBytes.Count;
            if (c >= 4 &&
                headerBytes[c - 4] == (byte)'\r' && headerBytes[c - 3] == (byte)'\n' &&
                headerBytes[c - 2] == (byte)'\r' && headerBytes[c - 1] == (byte)'\n')
            {
                break;
            }
            if (c > 64 * 1024) return null; // ヘッダが異常に長い → 拒否
        }
        if (headerBytes.Count == 0) return null;

        var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
        var lines = headerText.Split("\r\n");
        if (lines.Length == 0) return null;

        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return null;
        var method = requestLine[0].ToUpperInvariant();
        var path   = requestLine[1];

        int contentLength = 0;
        string? origin = null;
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line.Substring(0, colon).Trim();
            var val = line.Substring(colon + 1).Trim();
            if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                int.TryParse(val, out contentLength);
            else if (key.Equals("Origin", StringComparison.OrdinalIgnoreCase))
                origin = val;
        }

        var body = "";
        if (contentLength > 0)
        {
            if (contentLength > 16 * 1024 * 1024) return null; // 16MB 超は拒否
            var buf = new byte[contentLength];
            var read = 0;
            while (read < contentLength)
            {
                var n = stream.Read(buf, read, contentLength - read);
                if (n <= 0) break;
                read += n;
            }
            body = Encoding.UTF8.GetString(buf, 0, read);
        }

        return new HttpRequest(method, path, body, origin);
    }

    /// <summary>HTTP/1.1 応答を書く (Connection: close)。CORS ヘッダを付ける。</summary>
    private static void WriteResponse(NetworkStream stream, int status, string reason,
                                      string? contentType, string body, string? origin)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body ?? "");
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {status} {reason}\r\n");
        if (contentType is not null)
            sb.Append($"Content-Type: {contentType}; charset=utf-8\r\n");
        sb.Append($"Content-Length: {bodyBytes.Length}\r\n");
        // CORS: localhost 由来 (もしくは Origin 無し) のみここに来るので echo して許可。
        if (!string.IsNullOrEmpty(origin))
        {
            sb.Append($"Access-Control-Allow-Origin: {origin}\r\n");
            sb.Append("Access-Control-Allow-Methods: POST, GET, OPTIONS\r\n");
            sb.Append("Access-Control-Allow-Headers: Content-Type, Mcp-Session-Id, Mcp-Protocol-Version, Authorization\r\n");
        }
        sb.Append("Connection: close\r\n");
        sb.Append("\r\n");

        var headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
        stream.Write(headerBytes, 0, headerBytes.Length);
        if (bodyBytes.Length > 0)
            stream.Write(bodyBytes, 0, bodyBytes.Length);
        stream.Flush();
    }

    /// <summary>Origin が許可されるか。Origin 無し (= ネイティブクライアント) は許可。
    /// localhost / 127.0.0.1 / [::1] のみ許可し、それ以外の web オリジンは拒否 (DNS リバインディング対策)。</summary>
    private static bool IsOriginAllowed(string? origin)
    {
        if (string.IsNullOrEmpty(origin)) return true;
        if (origin.Equals("null", StringComparison.OrdinalIgnoreCase)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        var h = uri.Host;
        return h.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || h == "127.0.0.1"
            || h == "::1"
            || h == "[::1]";
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
