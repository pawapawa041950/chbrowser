using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;

namespace ChBrowser.Services.Api;

/// <summary>
/// 5ch.io への HTTP 通信の共通基盤。
/// Monazilla 規約に沿った User-Agent をデフォルトで付与する。
/// 各エンドポイント別クライアント (BbsmenuClient, DatClient, PostClient 等) はこれを共有して使う。
/// </summary>
public sealed class MonazillaClient : IDisposable
{
    public HttpClient Http { get; }

    private readonly RequestTimeoutHandler _timeout;

    /// <summary>通常の通信 1 回あたりの制限時間 (設定 → 通信「タイムアウト」)。
    /// 提供者専用の経路 (reddit の WebView2 セッション) を通る要求には掛けない: そちらはログイン窓でユーザを待つことがあり、
    /// その時間まで数えると、ログインして戻ってきた時には要求が打ち切られている (= 「失敗」になる)。
    /// 経路側は実際の取得 1 回ごとに自前の制限時間を持つ。</summary>
    public TimeSpan RequestTimeout
    {
        get => _timeout.Timeout;
        set => _timeout.Timeout = value;
    }

    public MonazillaClient(HttpMessageHandler? handler = null)
    {
        handler ??= new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = false, // Cookie はサービス側で個別管理 (どんぐり等)
        };

        // 提供者専用の通信経路 (reddit の WebView2 セッション等) に回す段を 1 つ挟む。経路が登録されていないホストは素通し。
        // 制限時間は HttpClient 全体ではなく、通常の通信の段 (RequestTimeoutHandler) にだけ掛ける (RequestTimeout 参照)。
        _timeout = new RequestTimeoutHandler(handler) { Timeout = TimeSpan.FromSeconds(30) };
        Http = new HttpClient(new ChBrowser.Services.Bbs.ProviderTransportHandler(_timeout), disposeHandler: true)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
        Http.DefaultRequestHeaders.UserAgent.Clear();
        Http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Monazilla", "1.00"));
        Http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ChBrowser", version));
    }

    public void Dispose() => Http.Dispose();
}

/// <summary>通常の通信 (提供者専用の経路を通らない要求) に制限時間を掛ける段。応答ヘッダが届くまでに
/// <see cref="Timeout"/> を超えたら、HttpClient のタイムアウトと同じく <see cref="TaskCanceledException"/> (内側に <see cref="TimeoutException"/>) にする。
/// 呼び出し側のキャンセルはそのまま <see cref="OperationCanceledException"/> として伝わる。</summary>
internal sealed class RequestTimeoutHandler : DelegatingHandler
{
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    public RequestTimeoutHandler(HttpMessageHandler inner) : base(inner) { }

    protected override async System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken ct)
    {
        using var cts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (Timeout != System.Threading.Timeout.InfiniteTimeSpan) cts.CancelAfter(Timeout);
        try
        {
            // 応答ヘッダまでを制限時間の対象にする (本文を逐次読む取得 = dat のストリーミング・動画の保存を妨げない)
            return await base.SendAsync(request, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && cts.IsCancellationRequested)
        {
            throw new TaskCanceledException($"通信がタイムアウトしました ({Timeout.TotalSeconds:0} 秒): {request.RequestUri}", new TimeoutException());
        }
    }
}
