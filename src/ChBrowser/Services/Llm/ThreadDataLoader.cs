using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Models;
using ChBrowser.Services.Api;
using ChBrowser.Services.Url;

namespace ChBrowser.Services.Llm;

/// <summary>open_thread_list_in_app に渡す 1 スレッドぶんの情報。
/// AI 側 (= list_threads や search_posts の結果から) で既に持っている情報をそのまま渡せる形にしてある。
/// PostCount=0 は「不明」のサイン (= 表示側で「?」相当を出す)。</summary>
public sealed record AiSearchResultEntry(string ThreadUrl, string Title, int PostCount);

/// <summary>
/// AI ツールから「いま開いていない別のスレ / 別の板」にアクセスするためのデータローダ。
/// 直接 HTTP は叩かず、既存の <see cref="DatClient"/> / <see cref="SubjectTxtClient"/> 経由で取得する
/// (= 通常の閲覧と同じキャッシュ規約に従う)。
///
/// <para><b>取得戦略</b>: 板一覧はメモリ上 (<c>BoardCategories</c>) から、スレ一覧 (subject.txt) と
/// dat はディスクキャッシュ優先、無ければネット取得。AI が突然 1000 スレの板に「list_threads」してきても
/// ローカルキャッシュ済みなら即返せる。</para>
///
/// <para><b>会話内キャッシュ</b>: 同じ会話で同じスレを連続して叩くケースに備え、ロード済み Post 列を
/// <see cref="MaxCachedThreads"/> 件まで LRU で保持する。AI チャットウィンドウが閉じれば全部破棄。</para>
/// </summary>
public sealed class ThreadDataLoader
{
    /// <summary>会話内 LRU キャッシュの上限スレ数。
    /// 1 スレ = 数百 KB レベルのことが多い (dat) のでメモリ圧迫を避けるためある程度小さく。</summary>
    private const int MaxCachedThreads = 8;

    private readonly SubjectTxtClient                    _subject;
    private readonly DatClient                           _dat;
    private readonly Func<IReadOnlyList<Board>>          _flatBoardsProvider;
    private readonly Func<string, string, string, Board> _resolveBoard;

    // (host:dir:key) → loaded posts。LRU で古いキーから捨てる。
    private readonly Dictionary<string, IReadOnlyList<Post>> _postsCache = new();
    private readonly LinkedList<string>                      _lruOrder   = new();
    private readonly object                                  _cacheLock  = new();

    public ThreadDataLoader(
        SubjectTxtClient                     subject,
        DatClient                            dat,
        Func<IReadOnlyList<Board>>           flatBoardsProvider,
        Func<string, string, string, Board>  resolveBoard)
    {
        _subject            = subject;
        _dat                = dat;
        _flatBoardsProvider = flatBoardsProvider;
        _resolveBoard       = resolveBoard;
    }

    /// <summary>BoardCategories から平坦化した全板の現在のスナップショットを返す。
    /// MainViewModel の <c>BoardCategories</c> をフラットに展開する callback を呼ぶだけ。</summary>
    public IReadOnlyList<Board> ListBoardsSnapshot() => _flatBoardsProvider();

    /// <summary>「(host, dir, key)」を AddressBarParser で取り出す。5ch.io / bbspink.com 系のみ受理。</summary>
    public bool TryParseThreadUrl(string url, out string host, out string dir, out string key)
    {
        host = dir = key = "";
        var t = AddressBarParser.Parse(url);
        if (t.Kind != AddressBarTargetKind.Thread) return false;
        host = t.Host; dir = t.Directory; key = t.ThreadKey;
        return true;
    }

    /// <summary>「(host, dir)」を AddressBarParser で取り出す (= 板 URL)。</summary>
    public bool TryParseBoardUrl(string url, out string host, out string dir)
    {
        host = dir = "";
        var t = AddressBarParser.Parse(url);
        if (t.Kind != AddressBarTargetKind.Board) return false;
        host = t.Host; dir = t.Directory;
        return true;
    }

    /// <summary>板のスレ一覧を取得。ディスクキャッシュ優先、無ければサーバ取得。</summary>
    public async Task<IReadOnlyList<ThreadInfo>> ListThreadsAsync(Board board, CancellationToken ct = default)
    {
        var disk = await _subject.LoadFromDiskAsync(board, ct).ConfigureAwait(false);
        if (disk.Count > 0) return disk;
        return await _subject.FetchAndSaveAsync(board, ct).ConfigureAwait(false);
    }

    /// <summary>指定スレの Post 列をロード。会話キャッシュ → ディスク → ネットの順。
    /// 戻り値はキャッシュエントリそのものが共有される (= caller は readonly として扱うこと)。</summary>
    public async Task<IReadOnlyList<Post>> LoadPostsAsync(Board board, string threadKey, CancellationToken ct = default)
    {
        var cacheKey = CacheKey(board.Host, board.DirectoryName, threadKey);

        lock (_cacheLock)
        {
            if (_postsCache.TryGetValue(cacheKey, out var cached))
            {
                TouchLru_NoLock(cacheKey);
                return cached;
            }
        }

        // ディスクに dat あればパース、無ければネット取得。
        IReadOnlyList<Post> posts;
        var disk = await _dat.LoadFromDiskAsync(board, threadKey, ct).ConfigureAwait(false);
        if (disk is not null && disk.Posts.Count > 0)
        {
            posts = disk.Posts;
        }
        else
        {
            var fetched = await _dat.FetchAsync(board, threadKey, ct).ConfigureAwait(false);
            posts = fetched.Posts;
        }

        lock (_cacheLock)
        {
            _postsCache[cacheKey] = posts;
            TouchLru_NoLock(cacheKey);
            EvictIfNeeded_NoLock();
        }
        return posts;
    }

    /// <summary>(host, dir) を canonical な Board に解決する。bbsmenu に登録があれば正規版、
    /// 無ければ fallback (= MainViewModel.ResolveBoard 経由)。</summary>
    public Board ResolveBoard(string host, string dir, string fallbackBoardName = "")
        => _resolveBoard(host, dir, fallbackBoardName);

    private static string CacheKey(string host, string dir, string key)
        => $"{host}:{dir}:{key}";

    private void TouchLru_NoLock(string key)
    {
        var node = _lruOrder.Find(key);
        if (node is not null) _lruOrder.Remove(node);
        _lruOrder.AddFirst(key);
    }

    private void EvictIfNeeded_NoLock()
    {
        while (_postsCache.Count > MaxCachedThreads && _lruOrder.Last is not null)
        {
            var oldest = _lruOrder.Last.Value;
            _lruOrder.RemoveLast();
            _postsCache.Remove(oldest);
        }
    }
}
