using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;
using ChBrowser.Services.Image;

namespace ChBrowser.Services.Media;

/// <summary>動画本体のバックグラウンドダウンロード管理 (Phase 4)。
///
/// <para>役割:
/// <list type="bullet">
/// <item><description>URL ベースでのコアレス: 同じ URL に対する複数 Request 呼び出しは
///   1 本の HttpClient ダウンロードに集約される (= スレッド側クリック + ビューワ起動 が同 URL の場合も帯域 1 本)</description></item>
/// <item><description>完了 / 失敗イベント: <see cref="DownloadCompleted"/> / <see cref="DownloadFailed"/> で
///   購読者 (= スレッド側 / ビューワ側) に通知 → 「未DL」バッジ除去や再描画のトリガ</description></item>
/// <item><description>キャッシュ済 URL は即時 <see cref="DownloadCompleted"/> 発火 (= 呼び出し側は
///   キャッシュ存否を気にせず Request() するだけで良い、というシンプル契約)</description></item>
/// </list></para>
///
/// <para>ダウンロードした bytes は <see cref="ImageCacheService.SaveAsync"/> 経由で
/// Kind=Video として永続化される (= .tmp に書いて atomic rename、size 上限チェック等は既存 SaveAsync 任せ)。</para>
///
/// <para>Phase 4 範囲では VideoDownloadManager クラス自体の提供のみ。
/// JS 側 (Phase 5+) からの「DL 要求」メッセージ受信や、状態問い合わせ応答は別フェーズで配線。</para></summary>
public sealed class VideoDownloadManager
{
    /// <summary>動画 DL 専用 HttpClient (ブラウザ UA)。
    /// 5ch 用の <c>MonazillaClient.Http</c> (= UA: Monazilla/1.00 ChBrowser/x.x) を流用すると、
    /// 外部 CDN (tadaup.jp 等) が UA で挙動を変えて Chrome とは異なるエンコード/ビットレートの
    /// ファイルを返す事例があるため (= 黒画面再生 / コーデック非対応の見かけ症状)、
    /// 通常のブラウザ UA を使う独自インスタンスを持つ。</summary>
    private readonly HttpClient        _http;
    private readonly ImageCacheService _cache;

    /// <summary>進行中ダウンロード: URL → 完了 Task (= TrySetResult されるまで Pending)。
    /// 同じ URL の Request() は同じ Task を共有するためコアレスが成立する。</summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _inFlight = new(StringComparer.Ordinal);

    /// <summary>過去のセッションで DL に失敗した URL (HTTP 4xx/5xx / ネットワークエラー / SaveAsync 失敗)。
    /// 同 URL を再 Request した時にも即 Failed 状態として扱う (= UI の「取得失敗」バッジを維持)。
    /// アプリ再起動でクリアされる。</summary>
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);
    private readonly object _failedLock = new();

    /// <summary>過去にサムネ抽出 (extractAndCacheVideoThumbnail / viewer.js 側 capture) で失敗した URL。
    /// 新たに同 URL のスロットが描画されたときに自動再試行をスキップするためのマーカ。
    /// ユーザ明示クリック (videoDownloadStart) でクリアされる。
    /// アプリ再起動でクリアされる。</summary>
    private readonly HashSet<string> _thumbFailed = new(StringComparer.Ordinal);
    private readonly object _thumbFailedLock = new();

    /// <summary>ダウンロードが正常完了したとき発火 (URL のみペイロード)。
    /// 引数は <see cref="ImageCacheService"/> にコミット済の状態で渡される (= 直後の Contains/TryGet で即取得可)。
    /// 既にキャッシュ済の URL に対する Request() でも同じイベントが (Task.Run 経由で) 発火する。</summary>
    public event EventHandler<VideoDownloadEventArgs>? DownloadCompleted;

    /// <summary>ダウンロードに失敗したとき発火 (URL のみペイロード)。
    /// HTTP エラー / ストリームエラー / SaveAsync 失敗のすべてで発火する。</summary>
    public event EventHandler<VideoDownloadEventArgs>? DownloadFailed;

    public VideoDownloadManager(ImageCacheService cache)
    {
        _cache = cache;
        // 動画は数 MB 〜数十 MB あり得るので 5 分タイムアウト (= MonazillaClient の 30 秒では切れる)。
        // AutomaticDecompression は動画 (.mp4) 用途では不要だが、サーバが万一 Transfer-Encoding で
        // 圧縮を入れてくる場合に備えて有効化。
        var handler = new System.Net.Http.HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            AllowAutoRedirect      = true,
            MaxAutomaticRedirections = 5,
        };
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        // ブラウザ UA で外部 CDN にアクセス (= Chrome と同じファイルを取得する)。
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36 ChBrowser");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ja,en;q=0.8");
    }

    /// <summary>URL のダウンロードを要求する。
    /// 既にキャッシュ済 → <see cref="DownloadCompleted"/> を即時発火 (Task.Run 経由) して false を返す。
    /// 既に in-flight → 何もせず false を返す (既存タスクの完了で購読者にイベントが届く)。
    /// 新規開始 → true を返す。</summary>
    public bool Request(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;

        // すでにキャッシュ済 → 統一フローのため Completed イベントを発火 (= 呼び出し側は分岐不要)。
        // 発火は Task.Run に逃がして re-entrance を回避 (Request 呼び出し中の subscriber が同じ
        // ロックを取り直すような構造でもデッドロックしないようにする)。
        if (_cache.Contains(url, CacheKind.Video))
        {
            _ = Task.Run(() => DownloadCompleted?.Invoke(this, new VideoDownloadEventArgs(url)));
            return false;
        }

        // 過去のセッションで失敗済 → 再 DL は試みず、Failed イベントだけ再発火 (UI バッジ維持用)。
        lock (_failedLock)
        {
            if (_failed.Contains(url))
            {
                _ = Task.Run(() => DownloadFailed?.Invoke(this, new VideoDownloadEventArgs(url)));
                return false;
            }
        }

        // TryAdd でアトミックに in-flight 登録。失敗 = 既に DL 中なので no-op (= 既存タスクに乗っかる)。
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_inFlight.TryAdd(url, tcs)) return false;

        _ = Task.Run(async () =>
        {
            bool ok = false;
            try
            {
                ok = await DownloadAsync(url).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VideoDownload] task crashed url={url}: {ex.Message}");
            }
            finally
            {
                _inFlight.TryRemove(url, out _);
                tcs.TrySetResult(ok);
                if (ok)
                {
                    DownloadCompleted?.Invoke(this, new VideoDownloadEventArgs(url));
                }
                else
                {
                    lock (_failedLock) { _failed.Add(url); }
                    DownloadFailed?.Invoke(this, new VideoDownloadEventArgs(url));
                }
            }
        });

        return true;
    }

    /// <summary>指定 URL の動画が現在ダウンロード進行中か。</summary>
    public bool IsDownloading(string url) => !string.IsNullOrEmpty(url) && _inFlight.ContainsKey(url);

    /// <summary>指定 URL が過去 (= このセッション中) に DL 失敗済か (404 / 5xx / ネットワークエラー等)。
    /// UI バッジ「取得失敗」表示の判定に使う。アプリ再起動でクリアされる。</summary>
    public bool IsFailed(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        lock (_failedLock) { return _failed.Contains(url); }
    }

    /// <summary>指定 URL の失敗状態をクリアする (= キャッシュ削除メニュー等から呼ばれる)。
    /// 次回 Request() で再 DL が試みられる。</summary>
    public void ResetFailedState(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        lock (_failedLock) { _failed.Remove(url); }
    }

    /// <summary>サムネ抽出失敗状態を記憶する。
    /// thread.js / viewer.js の抽出失敗メッセージ受信時に呼ばれる。
    /// 次回スレッド表示時にこの URL の自動抽出をスキップさせる用途。</summary>
    public void MarkThumbFailed(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        lock (_thumbFailedLock) { _thumbFailed.Add(url); }
    }

    /// <summary>指定 URL がサムネ抽出失敗済か。状態 push の <c>thumbExtractFailed</c> フィールドに乗る。</summary>
    public bool IsThumbFailed(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        lock (_thumbFailedLock) { return _thumbFailed.Contains(url); }
    }

    /// <summary>サムネ抽出失敗状態をクリアする。
    /// ユーザの明示クリック (= videoDownloadStart) で「再試行したい」意思があるとみなして呼ばれる。</summary>
    public void ResetThumbFailedState(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        lock (_thumbFailedLock) { _thumbFailed.Remove(url); }
    }

    private async Task<bool> DownloadAsync(string url)
    {
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Debug.WriteLine($"[VideoDownload] HTTP {(int)resp.StatusCode} url={url}");
                return false;
            }
            var contentType = resp.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrEmpty(contentType))
            {
                // Content-Type 不明時は URL 拡張子から推定するため SaveAsync 側に任せる (= "video/mp4" でも可)。
                contentType = "video/mp4";
            }

            using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await _cache.SaveAsync(url, stream, contentType, CacheKind.Video).ConfigureAwait(false);

            // SaveAsync は MaxFileBytes 超過などで silently skip することがあるので、
            // 最終的にキャッシュに乗ったかを Contains で再確認 (= 上限超過時は失敗扱い)。
            return _cache.Contains(url, CacheKind.Video);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VideoDownload] failed url={url}: {ex.Message}");
            return false;
        }
    }
}

/// <summary>ダウンロード完了/失敗イベントのペイロード (Phase 4)。</summary>
public sealed class VideoDownloadEventArgs : EventArgs
{
    public string Url { get; }
    public VideoDownloadEventArgs(string url) { Url = url; }
}
