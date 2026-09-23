using System.Collections.Generic;
using System.Text;
using ChBrowser.ViewModels;

namespace ChBrowser.Services.Render;

/// <summary>
/// 板一覧ペイン (Phase 14a)。ページ (シェル) は 1 回だけ読み込み、中身は JSON で送って JS が組み立てる
/// (<c>setBoardTree</c> メッセージ。スレ表示と同じ「シェル + データ push」方式)。
/// 板一覧を 1 掲示板分だけ取り直したときは、その掲示板のノードだけを送って差し替える
/// (= ページの再読み込みが無いので、スクロール位置・選択中の板・絞り込み・他の掲示板の開閉がそのまま残る)。
///
/// 階層は JS 側で <c>&lt;details&gt;/&lt;summary&gt;</c> として組むので、Edge ネイティブ Ctrl+F が
/// 閉じたカテゴリ内の板名にマッチしたとき自動でカテゴリを開いてくれる (Chromium の find-in-page 標準動作)。
/// </summary>
public static class BoardListHtmlBuilder
{
    private static string? _shellHtmlCache;
    private static readonly object Lock = new();

    /// <summary>板一覧ペインのシェル HTML (中身は空。JS が <c>ready</c> を送り、C# が <c>setBoardTree</c> で中身を送る)。</summary>
    public static string BuildShell() => LoadShellHtml();

    /// <summary>提供者 (5ch / まちBBS …) をトップノードにした 3 階層 (提供者 &gt; カテゴリ &gt; 板) の送信データ。
    /// <paramref name="providers"/> の順に並べ、各提供者の配下にその提供者のカテゴリを入れる。</summary>
    public static IReadOnlyList<BoardTreeProvider> BuildTree(IReadOnlyList<BoardListProviderNode> providers, IReadOnlyList<BoardCategoryViewModel> categories)
    {
        var list = new List<BoardTreeProvider>(providers.Count);
        foreach (var pv in providers)
        {
            var cats = new List<BoardTreeCategory>();
            var total = 0;
            foreach (var c in categories)
            {
                if (c.ProviderId != pv.Id) continue;
                var boards = new List<BoardTreeBoard>(c.Boards.Count);
                foreach (var bvm in c.Boards) boards.Add(new BoardTreeBoard(bvm.Board.Host, bvm.Board.DirectoryName, bvm.Board.BoardName));
                total += boards.Count;
                cats.Add(new BoardTreeCategory(c.CategoryName, c.IsExpanded, boards));
            }
            // 板一覧が空のときの案内 (提供者固有の文言があればそれ)
            string? empty = null;
            if (pv.HasBoardList && cats.Count == 0)
                empty = ChBrowser.Services.Bbs.BbsRegistry.FindById(pv.Id)?.BoardListEmptyHint is { Length: > 0 } hint
                    ? hint
                    : $"未取得 (メニューの 板一覧 → {pv.DisplayName}板一覧更新)";
            list.Add(new BoardTreeProvider(pv.Id, pv.DisplayName, pv.IsExpanded, pv.HasBoardList, pv.SupportsSearch,
                pv.HasBoardList ? total : null, empty, cats));
        }
        return list;
    }

    /// <summary>シェル HTML キャッシュをクリア (Phase 11d「すべての CSS を再読み込み」用)。</summary>
    public static void InvalidateCache()
    {
        lock (Lock) _shellHtmlCache = null;
    }

    private static string LoadShellHtml()
    {
        if (_shellHtmlCache is not null) return _shellHtmlCache;
        lock (Lock)
        {
            if (_shellHtmlCache is not null) return _shellHtmlCache;
            var html   = EmbeddedAssets.Read("board-list.html");
            var css    = EmbeddedAssets.ReadCss("board-list.css");  // disk-first
            var js     = EmbeddedAssets.Read("board-list.js");
            var bridge = EmbeddedAssets.Read("shortcut-bridge.js");
            // 絵文字フォント (Noto Color Emoji) が有効なら絵文字グリフだけ Noto に回す (テキストは据え置き)。
            var emojiCss = ChBrowser.Services.Fonts.EmojiFontService
                .BuildBodyFontCssOrNull("'Segoe UI','Yu Gothic UI','Meiryo'", "sans-serif") ?? "";
            _shellHtmlCache = html
                .Replace("<!--{{ITEMS}}-->",        "")
                .Replace("/*{{CSS}}*/",             css + emojiCss)
                .Replace("/*{{SHORTCUT_BRIDGE}}*/", bridge)
                .Replace("/*{{JS}}*/",              js);
            return _shellHtmlCache;
        }
    }
}

/// <summary>板一覧ペインのトップノード 1 つ (= 掲示板提供者)。</summary>
/// <param name="HasBoardList">板一覧を取得できる掲示板か。false (したらば / reddit) なら「検索」「表示済み板」の項目を出す。</param>
public sealed record BoardListProviderNode(string Id, string DisplayName, bool IsExpanded, bool HasBoardList = true, bool SupportsSearch = false);

/// <summary>板一覧ペインへ送る掲示板ノード 1 つ。<see cref="BoardCount"/> は板一覧を持つ掲示板だけ (見出しの件数)、
/// <see cref="EmptyMessage"/> は板一覧が空のときの案内。板一覧を持たない掲示板は「検索」「表示済み板」の項目になる。</summary>
public sealed record BoardTreeProvider(string Id, string Name, bool Expanded, bool HasBoardList, bool SupportsSearch,
                                       int? BoardCount, string? EmptyMessage, IReadOnlyList<BoardTreeCategory> Categories);

public sealed record BoardTreeCategory(string Name, bool Expanded, IReadOnlyList<BoardTreeBoard> Boards);

public sealed record BoardTreeBoard(string Host, string Dir, string Name);
