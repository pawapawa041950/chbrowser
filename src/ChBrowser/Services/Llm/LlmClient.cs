using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Models;

namespace ChBrowser.Services.Llm;

/// <summary>LLM 接続テストの結果。<see cref="Ok"/> が成否、<see cref="Message"/> が UI に出す詳細。</summary>
public sealed record LlmTestResult(bool Ok, string Message);

/// <summary>Chat Completions の 1 メッセージ。<see cref="Role"/> は "system" / "user" / "assistant"。</summary>
public sealed record LlmChatMessage(string Role, string Content);

/// <summary>Chat Completions (ストリーミング) の最終結果。
/// 成功なら <see cref="Content"/> に応答全文、失敗なら <see cref="Error"/> に理由。</summary>
public sealed record LlmChatResult(bool Ok, string Content, string? Error);

/// <summary>
/// OpenAI 互換 Chat Completions API のクライアント。接続確認 (<see cref="TestConnectionAsync"/>) と
/// ストリーミングチャット (<see cref="ChatStreamAsync"/>) を提供する。
///
/// エンドポイントは「base URL (例: https://api.openai.com/v1)」でも
/// 「/chat/completions まで含む完全 URL」でも受け付ける (= <see cref="BuildChatCompletionsUrl"/> で正規化)。
///
/// タイムアウトは <see cref="HttpClient.Timeout"/> ではなくメソッドごとの <see cref="CancellationTokenSource"/>
/// で制御する (= ストリーミングは応答全体が長くなり得るため、HttpClient.Timeout だと長文生成が途中で切れる)。
/// </summary>
public sealed class LlmClient : IDisposable
{
    /// <summary>接続確認のタイムアウト。</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);
    /// <summary>ストリーミングチャット全体のタイムアウト (= 長文生成も許容できる長さ)。</summary>
    private static readonly TimeSpan ChatTimeout = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;

    public LlmClient()
    {
        // LLM API は 5ch 通信とは無関係なので MonazillaClient とは別の HttpClient を持つ。
        // Timeout は無限にし、各メソッドが CancellationTokenSource.CancelAfter で個別に制限する
        // (= ストリーミング応答が HttpClient.Timeout で途中切断されるのを防ぐ)。
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>指定設定で OpenAI 互換 API に最小リクエスト (max_tokens=1 の "ping") を投げ、接続可否を返す。
    /// /chat/completions を実際に叩くので、URL の到達性・API キーの有効性・モデル名の正しさを
    /// まとめて検証できる (トークン消費はごく僅か)。</summary>
    public async Task<LlmTestResult> TestConnectionAsync(LlmSettings settings, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiUrl))
            return new LlmTestResult(false, "API URL が未設定です");
        if (string.IsNullOrWhiteSpace(settings.Model))
            return new LlmTestResult(false, "モデル名が未設定です");

        var endpoint = BuildChatCompletionsUrl(settings.ApiUrl);
        if (endpoint is null)
            return new LlmTestResult(false, $"API URL の形式が不正です: {settings.ApiUrl}");

        var payload = new
        {
            model      = settings.Model.Trim(),
            messages   = new[] { new { role = "user", content = "ping" } },
            max_tokens = 1,
        };
        var json = JsonSerializer.Serialize(payload);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TestTimeout);
        try
        {
            using var req = BuildRequest(endpoint, json, settings.ApiKey);
            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

            if (resp.IsSuccessStatusCode)
                return new LlmTestResult(true, $"接続成功 (HTTP {(int)resp.StatusCode})");

            // エラー時は OpenAI 互換のエラー JSON から message を拾えれば優先表示。
            var detail = ExtractErrorMessage(body) ?? Truncate(body, 200);
            return new LlmTestResult(false, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {detail}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new LlmTestResult(false, "タイムアウトしました");
        }
        catch (Exception ex)
        {
            return new LlmTestResult(false, $"接続エラー: {ex.Message}");
        }
    }

    /// <summary>OpenAI 互換 Chat Completions に system + 会話履歴を投げ、応答を SSE ストリーミングで受け取る。
    /// 受信したテキスト断片は到着のたびに <paramref name="onDelta"/> に渡される。
    /// 戻り値の <see cref="LlmChatResult.Content"/> には応答全文 (= 全 delta の連結) が入る。
    ///
    /// <para><b>スレッド注意</b>: <paramref name="onDelta"/> は本メソッドの await 継続と同じ同期コンテキストで
    /// 呼ばれる (= 内部は <c>ConfigureAwait(true)</c>)。UI スレッドから呼べば onDelta も UI スレッドで走るので、
    /// 呼び出し側はそのまま WebView2 への post 等ができる。</para></summary>
    public async Task<LlmChatResult> ChatStreamAsync(
        LlmSettings settings,
        IReadOnlyList<LlmChatMessage> messages,
        Action<string> onDelta,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiUrl))
            return new LlmChatResult(false, "", "API URL が未設定です");
        if (string.IsNullOrWhiteSpace(settings.Model))
            return new LlmChatResult(false, "", "モデル名が未設定です");
        if (messages.Count == 0)
            return new LlmChatResult(false, "", "送信するメッセージがありません");

        var endpoint = BuildChatCompletionsUrl(settings.ApiUrl);
        if (endpoint is null)
            return new LlmChatResult(false, "", $"API URL の形式が不正です: {settings.ApiUrl}");

        var payload = new
        {
            model    = settings.Model.Trim(),
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            stream   = true,
        };
        var json = JsonSerializer.Serialize(payload);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ChatTimeout);
        try
        {
            using var req = BuildRequest(endpoint, json, settings.ApiKey);
            // ResponseHeadersRead: 本文を待たずにヘッダ到着で戻る (= ストリーミング読み出しの前提)。
            using var resp = await _http
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(true);

            if (!resp.IsSuccessStatusCode)
            {
                var body   = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(true);
                var detail = ExtractErrorMessage(body) ?? Truncate(body, 300);
                return new LlmChatResult(false, "", $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {detail}");
            }

            using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(true);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var full = new StringBuilder();
            string? line;
            // SSE: "data: {json}" 行が delta、"data: [DONE]" で終端。空行・コメント行は読み飛ばす。
            while ((line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(true)) is not null)
            {
                if (line.Length == 0) continue;
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line.Substring(5).Trim();
                if (data.Length == 0) continue;
                if (data == "[DONE]") break;

                var delta = ExtractStreamDelta(data);
                if (!string.IsNullOrEmpty(delta))
                {
                    full.Append(delta);
                    onDelta(delta);
                }
            }

            return new LlmChatResult(true, full.ToString(), null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new LlmChatResult(false, "", "タイムアウトしました");
        }
        catch (Exception ex)
        {
            return new LlmChatResult(false, "", $"通信エラー: {ex.Message}");
        }
    }

    /// <summary>POST リクエストを組み立てる (JSON ボディ + 任意の Bearer 認証ヘッダ)。
    /// API キーが空ならヘッダ無しで送る (= 認証不要なローカル LLM サーバ等に対応)。</summary>
    private static HttpRequestMessage BuildRequest(string endpoint, string json, string apiKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        return req;
    }

    /// <summary>SSE の 1 chunk JSON から choices[0].delta.content を取り出す。
    /// 形が違う / content が無い (= role だけの先頭 chunk 等) 場合は null。</summary>
    private static string? ExtractStreamDelta(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (!doc.RootElement.TryGetProperty("choices", out var choices)) return null;
            if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return null;
            var first = choices[0];
            if (!first.TryGetProperty("delta", out var delta)) return null;
            if (!delta.TryGetProperty("content", out var content)) return null;
            return content.GetString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>設定された URL を /chat/completions エンドポイントに正規化する。
    /// 既に /chat/completions で終わっていればそのまま、それ以外は base URL とみなして付与する。
    /// http/https 以外のスキーム・パース不能な文字列なら null。</summary>
    private static string? BuildChatCompletionsUrl(string apiUrl)
    {
        var trimmed = apiUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;

        var s = trimmed.TrimEnd('/');
        if (s.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return s;
        return s + "/chat/completions";
    }

    /// <summary>OpenAI 互換のエラー JSON ( { "error": { "message": "..." } } または { "error": "..." } )
    /// から message 文字列を取り出す。JSON でない / 形が違う場合は null。</summary>
    private static string? ExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var msg))
                    return msg.GetString();
                if (err.ValueKind == JsonValueKind.String)
                    return err.GetString();
            }
        }
        catch
        {
            // JSON でない (= HTML エラーページ等) ならそのまま呼び出し元で本文を truncate して出す。
        }
        return null;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max) + "…";

    public void Dispose() => _http.Dispose();
}
