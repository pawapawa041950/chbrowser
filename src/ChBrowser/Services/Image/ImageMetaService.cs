using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ChBrowser.Services.Image;

/// <summary>
/// 外部画像 URL の HEAD リクエストでサイズを事前確認するサービス。
/// JS 側 (thread.js) は HEAD 結果を見て、しきい値超過なら自動ロードを止めて
/// 「クリックで読み込む」プレースホルダ表示にする。
/// </summary>
/// <remarks>
/// 5ch.io 通信ではなく外部画像ホスト (i.imgur.com 等) 向けなので、専用 HttpClient を持つ。
/// User-Agent は通常のブラウザ風 (Monazilla/1.00 ではない) — imgur 等は UA で弾かない想定だが、
/// 念のため Chrome の代表的な UA を名乗る。
///
/// キャッシュは URL → 結果 Task の in-memory のみ。同じ URL に対する HEAD 要求は 1 回で済む。
/// セッションをまたいだ永続化は (Phase 6 続き) idx.json または専用ファイルで行う想定。
/// </remarks>
public sealed class ImageMetaService : IDisposable
{
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, Task<ImageMeta>> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(initialCount: 6); // 同時 HEAD 上限 (帯域とサーバ負荷に配慮)

    public ImageMetaService()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression   = DecompressionMethods.All,
            AllowAutoRedirect        = true,
            MaxAutomaticRedirections = 5,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 ChBrowser/0.1");
    }

    /// <summary>同じ URL に対する HEAD 要求は in-flight Task を共有する。</summary>
    public Task<ImageMeta> GetAsync(string url) => _cache.GetOrAdd(url, FetchAsync);

    private async Task<ImageMeta> FetchAsync(string url)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                Debug.WriteLine($"[ImageMeta] HEAD {url} → {(int)res.StatusCode}");
                return ImageMeta.Unknown;
            }
            var size = res.Content.Headers.ContentLength;
            return new ImageMeta(Ok: true, Size: size);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ImageMeta] HEAD {url} failed: {ex.Message}");
            return ImageMeta.Unknown;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}

/// <summary>HEAD 結果。Ok=false は HEAD 失敗 (= サイズ不明、JS 側はそのまま読み込む)。</summary>
public readonly record struct ImageMeta(bool Ok, long? Size)
{
    public static ImageMeta Unknown => new(false, null);
}
