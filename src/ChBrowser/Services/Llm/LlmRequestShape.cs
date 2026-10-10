using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace ChBrowser.Services.Llm;

/// <summary>接続先 (エンドポイント + モデル) ごとに覚える「要求の形」。OpenAI 互換 API は接続先によって受け付ける項目が違う
/// (例: OpenAI の新しいモデルは <c>max_tokens</c> を拒否し <c>max_completion_tokens</c> を求める / 厳格なサーバは知らない項目を拒否する /
/// llama.cpp・vLLM は知らない項目を無視する)。接続先ごとの固有コードは持たず、拒否されたときのエラーから
/// 「どの項目がだめだったか」を読み取って形を直し、覚える (<see cref="TryAdapt"/>)。
///
/// <list type="bullet">
/// <item><description>出力上限の項目は <c>max_tokens</c> から始め、拒否されたら <c>max_completion_tokens</c>、両方だめなら送らない。</description></item>
/// <item><description>それ以外の任意の項目 (<c>stream_options</c> / <c>tool_choice</c> / 推論オフのヒント等) は、拒否されたら以後送らない。</description></item>
/// <item><description>必須の項目 (<c>model</c> / <c>messages</c> / <c>stream</c> / <c>tools</c>) は変えない (変えると意味が変わる)。</description></item>
/// <item><description>パスが違って 404 なら <c>/v1</c> を補って試す (base URL を <c>https://api.openai.com</c> のように書いた場合)。</description></item>
/// </list>
/// 覚えるのはアプリ起動中だけ (次回起動時に 1 回だけ学び直す)。</summary>
internal sealed class LlmRequestShape
{
    /// <summary>出力上限の項目の候補 (先頭から試す)。</summary>
    public static readonly string[] TokenLimitKeys = { "max_tokens", "max_completion_tokens" };
    /// <summary>形を変えない (落とさない) 項目。</summary>
    public static readonly HashSet<string> RequiredKeys = new(StringComparer.Ordinal) { "model", "messages", "stream", "tools" };

    private readonly object _lock = new();
    private readonly HashSet<string> _dropped   = new(StringComparer.Ordinal);
    private readonly HashSet<string> _knownGood = new(StringComparer.Ordinal);
    private readonly HashSet<string> _triedTokenKeys = new(StringComparer.Ordinal);
    private string  _endpoint;
    private string? _tokenLimitKey = TokenLimitKeys[0];

    public LlmRequestShape(string endpoint) => _endpoint = endpoint;

    /// <summary>形がまだ確かめられていない項目を含む要求は 1 本ずつ送る (並行の要求が同じエラーで一斉に失敗しないように)。</summary>
    public SemaphoreSlim ProbeLock { get; } = new(1, 1);

    public string Endpoint { get { lock (_lock) return _endpoint; } }

    /// <summary>出力上限に使う項目名 (null = 送らない)。</summary>
    public string? TokenLimitKey { get { lock (_lock) return _tokenLimitKey; } }

    public bool IsDropped(string key) { lock (_lock) return _dropped.Contains(key); }

    /// <summary>この要求の項目がすべて受け付けられると分かっているか。</summary>
    public bool AllKnownGood(IEnumerable<string> keys) { lock (_lock) return keys.All(_knownGood.Contains); }

    /// <summary>要求が通った: その項目は受け付けられると覚える。</summary>
    public void MarkGood(IEnumerable<string> keys) { lock (_lock) foreach (var k in keys) _knownGood.Add(k); }

    /// <summary>拒否された要求 (<paramref name="status"/> と応答本文) から、形を直せるなら直して何を変えたかを返す (直せなければ null)。
    /// <paramref name="sentKeys"/> は送った要求の最上位の項目名。</summary>
    public string? TryAdapt(int status, string? body, IReadOnlyCollection<string> sentKeys)
    {
        lock (_lock)
        {
            if (status == 404 && TryInsertV1(out var newEndpoint))
            {
                _endpoint = newEndpoint;
                return $"パスに /v1 を補う ({newEndpoint})";
            }
            if (status is not (400 or 404 or 409 or 415 or 422)) return null;

            var (text, param) = ReadError(body);
            if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(param)) return null;

            var optional = sentKeys.Where(k => !RequiredKeys.Contains(k)).ToList();
            var blamed = param is { Length: > 0 } && optional.Contains(param)
                ? new List<string> { param }
                : optional.Where(k => Mentions(text, k)).ToList();
            if (blamed.Count == 0) return null;

            var changes = new List<string>();
            foreach (var key in blamed)
            {
                if (key == _tokenLimitKey)
                {
                    _triedTokenKeys.Add(key);
                    _knownGood.Remove(key);
                    _tokenLimitKey = TokenLimitKeys.FirstOrDefault(k => !_triedTokenKeys.Contains(k));
                    changes.Add(_tokenLimitKey is null ? $"出力上限 ({key}) を送らない" : $"出力上限を {key} → {_tokenLimitKey}");
                }
                else if (_dropped.Add(key))
                {
                    _knownGood.Remove(key);
                    changes.Add($"{key} を送らない");
                }
            }
            return changes.Count > 0 ? string.Join(", ", changes) : null;
        }
    }

    private bool TryInsertV1(out string endpoint)
    {
        endpoint = _endpoint;
        if (!Uri.TryCreate(_endpoint, UriKind.Absolute, out var uri)) return false;
        if (uri.AbsolutePath.Contains("/v1/", StringComparison.OrdinalIgnoreCase)) return false;
        if (!uri.AbsolutePath.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return false;
        var basePath = uri.AbsolutePath[..^"/chat/completions".Length].TrimEnd('/');
        endpoint = new UriBuilder(uri) { Path = basePath + "/v1/chat/completions" }.Uri.ToString();
        return true;
    }

    /// <summary>エラー本文の文 (どこにあっても拾う) と、問題の項目名 (OpenAI 形式の <c>error.param</c> があれば)。</summary>
    internal static (string Text, string? Param) ReadError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return ("", null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string? param = null;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var err)
                && err.ValueKind == JsonValueKind.Object && err.TryGetProperty("param", out var p) && p.ValueKind == JsonValueKind.String)
                param = p.GetString();
            return (root.GetRawText(), param);   // 文はエラー JSON 全体 (message / detail / error 等、形を問わず項目名を探す)
        }
        catch (JsonException)
        {
            return (body, null);
        }
    }

    /// <summary>エラーの文に項目名が単語として出てくるか (<c>'max_tokens'</c> / <c>max_tokens</c> / <c>["stream_options"]</c> 等)。</summary>
    internal static bool Mentions(string text, string key)
        => Regex.IsMatch(text, @"(?<![A-Za-z0-9_])" + Regex.Escape(key) + @"(?![A-Za-z0-9_])");
}

/// <summary>接続先ごとの <see cref="LlmRequestShape"/> (エンドポイント + モデルで区別。アプリ起動中だけ覚える)。</summary>
internal static class LlmRequestShapes
{
    private static readonly ConcurrentDictionary<string, LlmRequestShape> Shapes = new(StringComparer.OrdinalIgnoreCase);

    public static LlmRequestShape For(string endpoint, string model)
        => Shapes.GetOrAdd(endpoint + "|" + model.Trim(), _ => new LlmRequestShape(endpoint));
}
