using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Services.Storage;

namespace ChBrowser.Services.Image;

/// <summary>
/// 画像のローカルディスクキャッシュ。
///
/// <para>
/// レイアウト: <c>data/cache/images/&lt;hh&gt;/&lt;hash&gt;.&lt;ext&gt;</c>
/// (hash = SHA256(url) の hex 全 64 文字、hh = その先頭 2 文字)。
/// </para>
///
/// <para>
/// インデックスは <c>data/cache/images/index.json</c> に hash キーで保存。同じ URL に対する
/// 重複格納は無く、<see cref="MaxBytes"/> 超過時に <c>last_access_at</c> 昇順で LRU 削除。
/// </para>
///
/// <para>
/// WebView2 の <c>WebResourceRequested</c> でキャッシュヒットを返し、
/// <c>WebResourceResponseReceived</c> でキャッシュミス時のレスポンスを保存する透過キャッシュ。
/// </para>
/// </summary>
public sealed class ImageCacheService : IDisposable
{
    private readonly string _imagesDir;
    private readonly string _indexPath;

    /// <summary>hash → entry。スレッド境界の保護のため操作はすべて <see cref="_indexLock"/> を取って行う。</summary>
    private readonly Dictionary<string, ImageCacheEntry> _index;
    private readonly object _indexLock = new();
    private readonly SemaphoreSlim _writeGate = new(initialCount: 1, maxCount: 1);

    /// <summary>キャッシュ全体の上限 (bytes)。デフォルト 1GB (Phase 11 設定画面で調整可能化予定)。</summary>
    public long MaxBytes { get; set; } = 1L * 1024 * 1024 * 1024;

    /// <summary>1 ファイルの上限 (bytes)。これを超えるレスポンスはキャッシュしない (RAM/IO 保護)。</summary>
    public long MaxFileBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>新規 entry/touch があったかどうか。dispose 時の flush 判定に使う。</summary>
    private bool _dirty;

    public ImageCacheService(DataPaths paths)
    {
        _imagesDir = paths.CacheImagesDir;
        _indexPath = Path.Combine(_imagesDir, "index.json");
        _index     = LoadIndex(_indexPath);
    }

    // ---------------------------------------------------------------
    // 公開 API
    // ---------------------------------------------------------------

    /// <summary>URL がキャッシュされていればその情報を返す。同時にメモリ上で last_access を更新。</summary>
    public bool TryGet(string url, out ImageCacheHit hit)
    {
        var hash = ComputeUrlHash(url);
        ImageCacheEntry? entry;
        lock (_indexLock)
        {
            if (!_index.TryGetValue(hash, out entry))
            {
                hit = default;
                return false;
            }
        }

        var path = HashToPath(entry.Hash, entry.Ext);
        if (!File.Exists(path))
        {
            // index に entry はあるがファイルが消えている → entry を破棄して miss 扱い
            lock (_indexLock)
            {
                _index.Remove(hash);
                _dirty = true;
            }
            hit = default;
            return false;
        }

        // touch (永続化は次の SaveAsync / Dispose まで遅延)
        entry.LastAccessAt = DateTimeOffset.UtcNow;
        lock (_indexLock) { _dirty = true; }

        hit = new ImageCacheHit(path, entry.ContentType, entry.Size);
        return true;
    }

    /// <summary>URL がキャッシュ済みかだけを真偽で返す (size 取得や touch はしない)。</summary>
    public bool Contains(string url)
    {
        var hash = ComputeUrlHash(url);
        lock (_indexLock)
        {
            if (!_index.TryGetValue(hash, out var entry)) return false;
            return File.Exists(HashToPath(entry.Hash, entry.Ext));
        }
    }

    /// <summary>レスポンスストリームを丸ごとディスクに書き、index に追加する。</summary>
    public async Task SaveAsync(string url, Stream content, string? contentType)
    {
        if (string.IsNullOrEmpty(url)) return;
        var hash = ComputeUrlHash(url);

        // 既に同じ hash のキャッシュがあれば touch だけして抜ける
        lock (_indexLock)
        {
            if (_index.ContainsKey(hash))
            {
                _index[hash].LastAccessAt = DateTimeOffset.UtcNow;
                _dirty = true;
                return;
            }
        }

        var ext  = ExtractExtension(url, contentType);
        var path = HashToPath(hash, ext);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // .tmp に書いてから rename (atomic 化、書き込み中の読み取り防止)
        var tmpPath = path + ".tmp";
        long size;
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            try
            {
                await using (var fs = File.Create(tmpPath))
                {
                    await content.CopyToAsync(fs).ConfigureAwait(false);
                    size = fs.Length;
                }

                if (size > MaxFileBytes || size <= 0)
                {
                    SafeDelete(tmpPath);
                    return;
                }

                // 既存ファイルがあれば置き換え
                if (File.Exists(path)) SafeDelete(path);
                File.Move(tmpPath, path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ImageCache] save failed for {url}: {ex.Message}");
                SafeDelete(tmpPath);
                return;
            }

            var now = DateTimeOffset.UtcNow;
            lock (_indexLock)
            {
                _index[hash] = new ImageCacheEntry
                {
                    Hash         = hash,
                    Url          = url,
                    Ext          = ext,
                    Size         = size,
                    ContentType  = contentType ?? "application/octet-stream",
                    FetchedAt    = now,
                    LastAccessAt = now,
                };
                _dirty = true;
            }

            await PersistIndexAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }

        // 上限超過なら LRU で間引く (バックグラウンド)
        _ = Task.Run(PruneIfOverLimitAsync);
    }

    // ---------------------------------------------------------------
    // LRU prune
    // ---------------------------------------------------------------

    private async Task PruneIfOverLimitAsync()
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            List<ImageCacheEntry> ordered;
            long totalSize;
            lock (_indexLock)
            {
                totalSize = _index.Values.Sum(e => e.Size);
                if (totalSize <= MaxBytes) return;
                ordered = _index.Values.OrderBy(e => e.LastAccessAt).ToList();
            }

            foreach (var e in ordered)
            {
                if (totalSize <= MaxBytes) break;
                var p = HashToPath(e.Hash, e.Ext);
                SafeDelete(p);
                lock (_indexLock)
                {
                    _index.Remove(e.Hash);
                    _dirty = true;
                }
                totalSize -= e.Size;
            }

            await PersistIndexAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // ---------------------------------------------------------------
    // index.json 読み書き
    // ---------------------------------------------------------------

    private static readonly JsonSerializerOptions IndexJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented        = false,
        Converters           = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static Dictionary<string, ImageCacheEntry> LoadIndex(string indexPath)
    {
        try
        {
            if (!File.Exists(indexPath)) return new Dictionary<string, ImageCacheEntry>(StringComparer.Ordinal);
            using var fs = File.OpenRead(indexPath);
            var dto = JsonSerializer.Deserialize<Dictionary<string, ImageCacheEntry>>(fs, IndexJsonOptions);
            return dto ?? new Dictionary<string, ImageCacheEntry>(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ImageCache] index load failed: {ex.Message}");
            return new Dictionary<string, ImageCacheEntry>(StringComparer.Ordinal);
        }
    }

    private async Task PersistIndexAsync()
    {
        // _writeGate を取って呼ぶ前提
        Dictionary<string, ImageCacheEntry> snapshot;
        lock (_indexLock)
        {
            if (!_dirty) return;
            snapshot = new Dictionary<string, ImageCacheEntry>(_index, StringComparer.Ordinal);
            _dirty   = false;
        }

        var tmp = _indexPath + ".tmp";
        try
        {
            await using (var fs = File.Create(tmp))
            {
                await JsonSerializer.SerializeAsync(fs, snapshot, IndexJsonOptions).ConfigureAwait(false);
            }
            if (File.Exists(_indexPath)) SafeDelete(_indexPath);
            File.Move(tmp, _indexPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ImageCache] index persist failed: {ex.Message}");
            SafeDelete(tmp);
        }
    }

    // ---------------------------------------------------------------
    // ヘルパ
    // ---------------------------------------------------------------

    private static string ComputeUrlHash(string url)
    {
        Span<byte> dest = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(url), dest);
        return Convert.ToHexString(dest).ToLowerInvariant();
    }

    private string HashToPath(string hash, string ext)
    {
        var sub = hash.Length >= 2 ? hash.Substring(0, 2) : "00";
        return Path.Combine(_imagesDir, sub, hash + ext);
    }

    private static string ExtractExtension(string url, string? contentType)
    {
        // URL のクエリ/フラグメントを除いた末尾を見る
        var u   = url;
        var cut = u.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0) u = u.Substring(0, cut);
        var ext = Path.GetExtension(u).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp") return ext;

        // URL に拡張子が無ければ Content-Type から推測
        return contentType?.ToLowerInvariant() switch
        {
            string ct when ct.StartsWith("image/jpeg") => ".jpg",
            string ct when ct.StartsWith("image/png")  => ".png",
            string ct when ct.StartsWith("image/gif")  => ".gif",
            string ct when ct.StartsWith("image/webp") => ".webp",
            _ => ".bin",
        };
    }

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Debug.WriteLine($"[ImageCache] delete failed {path}: {ex.Message}"); }
    }

    public void Dispose()
    {
        // 最後の touch / dirty を flush
        try
        {
            _writeGate.Wait(TimeSpan.FromSeconds(2));
            try { PersistIndexAsync().GetAwaiter().GetResult(); }
            finally { _writeGate.Release(); }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ImageCache] dispose flush failed: {ex.Message}");
        }
        _writeGate.Dispose();
    }
}

/// <summary>キャッシュヒットの結果。</summary>
public readonly record struct ImageCacheHit(string FilePath, string ContentType, long Size);

/// <summary>キャッシュ index の 1 entry。<c>_index</c> から見ると Key = Hash になる。</summary>
public sealed class ImageCacheEntry
{
    public string Hash         { get; set; } = "";
    public string Url          { get; set; } = "";
    public string Ext          { get; set; } = "";
    public long   Size         { get; set; }
    public string ContentType  { get; set; } = "";
    public DateTimeOffset FetchedAt    { get; set; }
    public DateTimeOffset LastAccessAt { get; set; }
}
