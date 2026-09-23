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

    public MonazillaClient(HttpMessageHandler? handler = null)
    {
        handler ??= new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = false, // Cookie はサービス側で個別管理 (どんぐり等)
        };

        // 提供者専用の通信経路 (reddit の WebView2 セッション等) に回す段を 1 つ挟む。経路が登録されていないホストは素通し。
        Http = new HttpClient(new ChBrowser.Services.Bbs.ProviderTransportHandler(handler), disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
        Http.DefaultRequestHeaders.UserAgent.Clear();
        Http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Monazilla", "1.00"));
        Http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ChBrowser", version));
    }

    public void Dispose() => Http.Dispose();
}
