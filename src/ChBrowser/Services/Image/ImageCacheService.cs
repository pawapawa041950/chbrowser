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

/// <summary>キャッシュエントリの種別。同じ URL でも Kind が違えば別エントリとして並存する
/// (= 動画 URL に対して「サムネ JPEG」と「動画本体 MP4」を両方持てる)。
/// 既存の画像エントリ (Phase 1 以前) は Kind 欄を持たないため、JSON 読み込み時に
/// デフォルト値 (= <see cref="Image"/>) となるよう、列挙の 0 を Image にしている。</summary>
public enum CacheKind
{
    Image      = 0,
    VideoThumb = 1,
    Video      = 2,
}

/// <summary>
/// 画像 / 動画サムネ / 動画本体のローカルディスクキャッシュ (Phase 動画キャッシュ拡張で名前は据置)。
///
/// <para>
/// レイアウト:
/// <list type="bullet">
/// <item><description>Image / VideoThumb: <c>data/cache/images/&lt;hh&gt;/&lt;hash&gt;.&lt;ext&gt;</c></description></item>
/// <item><description>Video: <c>data/cache/videos/&lt;hh&gt;/&lt;hash&gt;.&lt;ext&gt;</c></description></item>
/// </list>
/// hash = SHA256(<c>kind-namespaced input</c>) の hex 全 64 文字、hh = その先頭 2 文字。
/// Image だけは <c>input = url</c> 素のままで既存キャッシュとハッシュ互換 (= アップグレードで失効しない)。
/// それ以外は <c>input = "videothumb:" + url</c> 等で名前空間分離して同 URL 衝突を防ぐ。
/// </para>
///
/// <para>
/// インデックスは <c>data/cache/images/index.json</c> に hash キーで集約保存 (Kind 列で区別)。
/// <see cref="MaxBytes"/> 超過時に <c>last_access_at</c> 昇順で LRU 削除。
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
    private readonly string _videosDir;
    private readonly string _indexPath;

    /// <summary>hash → entry。スレッド境界の保護のため操作はすべて <see cref="_indexLock"/> を取って行う。</summary>
    private readonly Dictionary<string, ImageCacheEntry> _index;
    private readonly object _indexLock = new();
    private readonly SemaphoreSlim _writeGate = new(initialCount: 1, maxCount: 1);

    /// <summary>キャッシュ全体の上限 (bytes)。デフォルト 1GB (Phase 11 設定画面で調整可能化予定)。
    /// 画像 / 動画サムネ / 動画本体すべての合計に対して効く (= 集約 LRU)。</summary>
    public long MaxBytes { get; set; } = 1L * 1024 * 1024 * 1024;

    /// <summary>1 ファイルの上限 (bytes、画像用、既存)。これを超えるレスポンスはキャッシュしない。</summary>
    public long MaxFileBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>1 ファイルの上限 (bytes、動画サムネ用)。サムネは縮小済 JPEG なので小さい上限で十分。</summary>
    public long MaxVideoThumbFileBytes { get; set; } = 8L * 1024 * 1024;

    /// <summary>1 ファイルの上限 (bytes、動画本体用)。5ch 共有動画は数 MB〜数十 MB が中心だが、
    /// 念のため余裕を持って 300MB まで許容する。</summary>
    public long MaxVideoFileBytes { get; set; } = 300L * 1024 * 1024;

    /// <summary>Kind 別の 1 ファイル上限を返す。<see cref="SaveAsync"/> 内で使う。</summary>
    public long GetMaxFileBytes(CacheKind kind) => kind switch
    {
        CacheKind.VideoThumb => MaxVideoThumbFileBytes,
        CacheKind.Video      => MaxVideoFileBytes,
        _                    => MaxFileBytes,
    };

    /// <summary>新規 entry/touch があったかどうか。dispose 時の flush 判定に使う。</summary>
    private bool _dirty;

    public ImageCacheService(DataPaths paths)
    {
        _imagesDir = paths.CacheImagesDir;
        _videosDir = paths.CacheVideosDir;
        _indexPath = Path.Combine(_imagesDir, "index.json");
        _index     = LoadIndex(_indexPath);
    }

    /// <summary>仮想ホストマッピング用に images / videos のルートディレクトリを公開する。
    /// CoreWebView2.SetVirtualHostNameToFolderMapping で <c>cache/</c> 直下を Allow マウントすると、
    /// <c>https://chbrowser-cache.local/images/...</c> / <c>/videos/...</c> でアクセス可能になる。</summary>
    public string CacheRootDir => Path.GetDirectoryName(_imagesDir)!;

    /// <summary>キャッシュフォルダの仮想ホスト名 (Phase 2)。
    /// WebView2 の <c>SetVirtualHostNameToFolderMapping</c> でこの名前 →
    /// <see cref="CacheRootDir"/> をマウントしておくと、
    /// <c>https://chbrowser-cache.local/images/aa/&lt;hash&gt;.jpg</c> や
    /// <c>https://chbrowser-cache.local/videos/aa/&lt;hash&gt;.mp4</c> で
    /// ローカルキャッシュファイルを <c>&lt;img&gt;</c>/<c>&lt;video&gt;</c> から参照できる。
    /// 本物の DNS と衝突しないよう RFC 6761 で予約されている <c>.local</c> ドメインを使用。</summary>
    public const string VirtualHostName = "chbrowser-cache.local";

    /// <summary>キャッシュヒットしたファイルを参照する仮想ホスト URL を組み立てる。
    /// <see cref="TryGet"/> の結果を受けて <c>&lt;video src=...&gt;</c> に渡せる形式にする用途。</summary>
    public string BuildVirtualHostUrl(ImageCacheHit hit)
    {
        var relative = Path.GetRelativePath(CacheRootDir, hit.FilePath).Replace('\\', '/');
        return $"https://{VirtualHostName}/{relative}";
    }

    // ---------------------------------------------------------------
    // 公開 API
    // ---------------------------------------------------------------

    /// <summary>URL + Kind の組み合わせでキャッシュされていればその情報を返す。同時にメモリ上で last_access を更新。
    /// kind を省略すると Image 扱い (既存呼び出し元の互換性確保)。</summary>
    public bool TryGet(string url, out ImageCacheHit hit, CacheKind kind = CacheKind.Image)
    {
        var hash = ComputeUrlHash(url, kind);
        ImageCacheEntry? entry;
        lock (_indexLock)
        {
            if (!_index.TryGetValue(hash, out entry))
            {
                hit = default;
                return false;
            }
        }

        var path = HashToPath(entry.Hash, entry.Ext, entry.Kind);
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

        hit = new ImageCacheHit(path, entry.ContentType, entry.Size, entry.Kind);
        return true;
    }

    /// <summary>URL + Kind がキャッシュ済みかだけを真偽で返す (size 取得や touch はしない)。
    /// kind を省略すると Image 扱い (既存呼び出し元の互換性確保)。</summary>
    public bool Contains(string url, CacheKind kind = CacheKind.Image)
    {
        var hash = ComputeUrlHash(url, kind);
        lock (_indexLock)
        {
            if (!_index.TryGetValue(hash, out var entry)) return false;
            return File.Exists(HashToPath(entry.Hash, entry.Ext, entry.Kind));
        }
    }

    /// <summary>指定 URL + Kind のキャッシュエントリを物理ファイル + index から削除する。
    /// 削除成功時 true、エントリが無かった場合 false。
    /// 右クリックメニュー「キャッシュ削除」等から呼ばれる。</summary>
    public bool Delete(string url, CacheKind kind = CacheKind.Image)
    {
        if (string.IsNullOrEmpty(url)) return false;
        var hash = ComputeUrlHash(url, kind);
        ImageCacheEntry? entry;
        lock (_indexLock)
        {
            if (!_index.TryGetValue(hash, out entry)) return false;
            _index.Remove(hash);
            _dirty = true;
        }
        var path = HashToPath(entry.Hash, entry.Ext, entry.Kind);
        SafeDelete(path);
        // index 永続化をバックグラウンドで予約 (= UI スレッドを止めない)。
        _ = Task.Run(async () =>
        {
            await _writeGate.WaitAsync().ConfigureAwait(false);
            try { await PersistIndexAsync().ConfigureAwait(false); }
            finally { _writeGate.Release(); }
        });
        return true;
    }

    /// <summary>指定 URL の全 Kind (image / video_thumb / video) のキャッシュエントリを削除する。
    /// 動画関連を一括クリアするユースケース用。</summary>
    public void DeleteAll(string url)
    {
        Delete(url, CacheKind.Image);
        Delete(url, CacheKind.VideoThumb);
        Delete(url, CacheKind.Video);
    }

    /// <summary>レスポンスストリームを丸ごとディスクに書き、index に追加する。
    /// kind を省略すると Image 扱い (既存呼び出し元の互換性確保)。
    /// 1 ファイル上限は <see cref="GetMaxFileBytes"/> で Kind ごとに切り替わる。</summary>
    public async Task SaveAsync(string url, Stream content, string? contentType, CacheKind kind = CacheKind.Image)
    {
        if (string.IsNullOrEmpty(url)) return;
        var hash = ComputeUrlHash(url, kind);

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

        var ext  = ExtractExtension(url, contentType, kind);
        var path = HashToPath(hash, ext, kind);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var perFileLimit = GetMaxFileBytes(kind);

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

                if (size > perFileLimit || size <= 0)
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
                Debug.WriteLine($"[ImageCache] save failed for {url} (kind={kind}): {ex.Message}");
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
                    Kind         = kind,
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
                var p = HashToPath(e.Hash, e.Ext, e.Kind);
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

    /// <summary>Kind 別に名前空間を分けて SHA256 を取る。Image だけ素の URL (= 既存キャッシュ互換)、
    /// VideoThumb / Video は <c>"videothumb-v2:" + url</c> 等で接頭辞化する。
    /// これにより同じ動画 URL に対するサムネ JPEG と動画本体が衝突しない。
    /// <para>VideoThumb は v2: Phase 5 初期実装のサムネ抽出が time=0 の黒フレームを保存していた問題により、
    /// 旧 namespace のキャッシュは無効化。v2 は seeked イベント待ちで有意なフレームを取得する。</para></summary>
    private static string ComputeUrlHash(string url, CacheKind kind)
    {
        var input = kind switch
        {
            CacheKind.Image      => url,
            CacheKind.VideoThumb => "videothumb-v2:" + url,
            CacheKind.Video      => "video:" + url,
            _                    => url,
        };
        Span<byte> dest = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(input), dest);
        return Convert.ToHexString(dest).ToLowerInvariant();
    }

    /// <summary>Kind 別の保存ディレクトリ + 2 桁 hh + ファイル名で full path 組み立て。
    /// Video のみ <c>_videosDir</c>、Image / VideoThumb は <c>_imagesDir</c>。</summary>
    private string HashToPath(string hash, string ext, CacheKind kind)
    {
        var sub  = hash.Length >= 2 ? hash.Substring(0, 2) : "00";
        var root = kind == CacheKind.Video ? _videosDir : _imagesDir;
        return Path.Combine(root, sub, hash + ext);
    }

    private static string ExtractExtension(string url, string? contentType, CacheKind kind)
    {
        // URL のクエリ/フラグメントを除いた末尾を見る
        var u   = url;
        var cut = u.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0) u = u.Substring(0, cut);
        var ext = Path.GetExtension(u).ToLowerInvariant();

        // VideoThumb は実体が JPEG (= canvas.toDataURL の出力) なので URL 拡張子は無視して .jpg 固定。
        if (kind == CacheKind.VideoThumb) return ".jpg";

        // Video の拡張子セット
        if (kind == CacheKind.Video)
        {
            if (ext is ".mp4" or ".webm" or ".mov") return ext;
            return contentType?.ToLowerInvariant() switch
            {
                string ct when ct.StartsWith("video/mp4")  => ".mp4",
                string ct when ct.StartsWith("video/webm") => ".webm",
                string ct when ct.StartsWith("video/quicktime") => ".mov",
                _ => ".bin",
            };
        }

        // Image の拡張子セット (既存)
        if (ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp") return ext;
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

/// <summary>キャッシュヒットの結果。<see cref="Kind"/> はヒットしたエントリの種別 (Image / VideoThumb / Video)。</summary>
public readonly record struct ImageCacheHit(string FilePath, string ContentType, long Size, CacheKind Kind);

/// <summary>キャッシュ index の 1 entry。<c>_index</c> から見ると Key = Hash になる。
/// <see cref="Kind"/> は Phase 1 で追加。既存 (Kind 列無し) の JSON は読み込み時にデフォルト = Image となる。</summary>
public sealed class ImageCacheEntry
{
    public string Hash         { get; set; } = "";
    public string Url          { get; set; } = "";
    public CacheKind Kind      { get; set; } = CacheKind.Image;
    public string Ext          { get; set; } = "";
    public long   Size         { get; set; }
    public string ContentType  { get; set; } = "";
    public DateTimeOffset FetchedAt    { get; set; }
    public DateTimeOffset LastAccessAt { get; set; }
}
