using System;
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

/// <summary>
/// OpenAI 互換 Chat Completions API のクライアント。
/// 現状は接続確認 (<see cref="TestConnectionAsync"/>) のみだが、将来の LLM 連携機能
/// (スレ要約・翻訳・NG 提案など) の通信基盤として共有して使う想定。
///
/// エンドポイントは「base URL (例: https://api.openai.com/v1)」でも
/// 「/chat/completions まで含む完全 URL」でも受け付ける (= <see cref="BuildChatCompletionsUrl"/> で正規化)。
/// </summary>
public sealed class LlmClient : IDisposable
{
    private readonly HttpClient _http;

    public LlmClient()
    {
        // LLM API は 5ch 通信とは無関係なので MonazillaClient とは別の HttpClient を持つ
        // (= Monazilla User-Agent を送らない、タイムアウトも長めに取る)。
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
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

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            // API キーが空ならヘッダ無しで送る (= 認証不要なローカル LLM サーバ等に対応)。
            if (!string.IsNullOrWhiteSpace(settings.ApiKey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (resp.IsSuccessStatusCode)
                return new LlmTestResult(true, $"接続成功 (HTTP {(int)resp.StatusCode})");

            // エラー時は OpenAI 互換のエラー JSON から message を拾えれば優先表示。
            var detail = ExtractErrorMessage(body) ?? Truncate(body, 200);
            return new LlmTestResult(false, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {detail}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new LlmTestResult(false, "タイムアウトしました");
        }
        catch (Exception ex)
        {
            return new LlmTestResult(false, $"接続エラー: {ex.Message}");
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
