using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ChBrowser.Services.Image;

/// <summary>
/// 画像ビューア (Phase 10b) の「保存」用サービス。
/// 画像はビューア表示時に <see cref="ImageCacheService"/> へ透過保存されているはずなので
/// まずそこからコピー、無ければ HTTP で取得して書き出す。
/// </summary>
public sealed class ImageSaver
{
    private readonly ImageCacheService _cache;
    private readonly HttpClient        _http;

    public ImageSaver(ImageCacheService cache, HttpClient http)
    {
        _cache = cache;
        _http  = http;
    }

    /// <summary>URL の末尾セグメントから保存ダイアログ用のファイル名候補を作る。
    /// 拡張子が無ければ <paramref name="contentTypeFallback"/> から推測 (.jpg がデフォルト)。</summary>
    public static string SuggestFileName(string url, string contentTypeFallback = "image/jpeg")
    {
        try
        {
            var uri  = new Uri(url, UriKind.Absolute);
            var seg  = uri.Segments;
            var last = seg.Length > 0 ? seg[^1].TrimEnd('/') : "image";
            // クエリは付かない (Segments は path のみ)
            if (string.IsNullOrEmpty(last)) last = uri.Host;
            // 拡張子が無ければ補う
            if (string.IsNullOrEmpty(Path.GetExtension(last)))
                last += ExtFromContentType(contentTypeFallback);
            // ファイル名として安全な文字に制限
            foreach (var bad in Path.GetInvalidFileNameChars())
                last = last.Replace(bad, '_');
            return last;
        }
        catch
        {
            return "image" + ExtFromContentType(contentTypeFallback);
        }
    }

    /// <summary>キャッシュにあればコピー、無ければ HTTP で fetch して <paramref name="destPath"/> に書き出す。</summary>
    public async Task SaveAsync(string url, string destPath, CancellationToken ct = default)
    {
        if (_cache.TryGet(url, out var hit))
        {
            File.Copy(hit.FilePath, destPath, overwrite: true);
            return;
        }

        // フォールバック: HTTP 直接 fetch
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Create(destPath);
        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
    }

    private static string ExtFromContentType(string contentType) => contentType.ToLowerInvariant() switch
    {
        var ct when ct.StartsWith("image/jpeg") => ".jpg",
        var ct when ct.StartsWith("image/png")  => ".png",
        var ct when ct.StartsWith("image/gif")  => ".gif",
        var ct when ct.StartsWith("image/webp") => ".webp",
        var ct when ct.StartsWith("image/")     => ".img",
        _ => ".bin",
    };
}
