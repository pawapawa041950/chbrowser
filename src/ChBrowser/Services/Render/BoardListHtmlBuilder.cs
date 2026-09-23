using System.Collections.Generic;
using System.Text;
using ChBrowser.ViewModels;

namespace ChBrowser.Services.Render;

/// <summary>
/// 板一覧 (Phase 14a) を 1 枚の HTML として組み立てる。
///
/// 階層は <c>&lt;details&gt;/&lt;summary&gt;</c> ベース。これにより Edge ネイティブ Ctrl+F が
/// 閉じたカテゴリ内の板/スレ名にマッチしたとき自動でカテゴリを開いてくれる
/// (Chromium の find-in-page 標準動作)。
///
/// HTML/CSS/JS の本体は埋め込みリソース <c>Resources/board-list.html</c> 等から読み出して
/// プレースホルダ <c>&lt;!--{{ITEMS}}--&gt;</c> に挿入する。スレ表示シェル (thread.html) と同じ構造。
/// </summary>
public static class BoardListHtmlBuilder
{
    private static string? _shellHtmlCache;
    private static readonly object Lock = new();

    /// <summary>従来 API (提供者ノード無しの 2 階層)。</summary>
    public static string Build(IReadOnlyList<BoardCategoryViewModel> categories)
        => LoadShellHtml().Replace("<!--{{ITEMS}}-->", BuildCategories(categories));

    /// <summary>提供者 (5ch / まちBBS …) をトップノードにした 3 階層 (提供者 &gt; カテゴリ &gt; 板)。
    /// <paramref name="providers"/> の順に並べ、各提供者の配下にその提供者のカテゴリを出す。
    /// カテゴリが 1 つも無い提供者は「未取得」の案内を出す。</summary>
    public static string Build(IReadOnlyList<BoardListProviderNode> providers, IReadOnlyList<BoardCategoryViewModel> categories)
    {
        var sb = new StringBuilder(8192);
        foreach (var pv in providers)
        {
            var own = new List<BoardCategoryViewModel>();
            foreach (var c in categories) if (c.ProviderId == pv.Id) own.Add(c);

            sb.Append(@"<details class=""provider""");
            if (pv.IsExpanded) sb.Append(@" open");
            sb.Append(@" data-provider=""").Append(HtmlEscape.Attr(pv.Id)).Append('"').Append('>');
            sb.Append(@"<summary class=""provider-name"">").Append(HtmlEscape.Text(pv.DisplayName));
            if (pv.HasBoardList) sb.Append(@" <span class=""provider-count"">").Append(CountBoards(own)).Append("</span>");
            sb.Append("</summary>");
            if (!pv.HasBoardList)
            {
                // 板一覧を持たない掲示板 (したらば / reddit): 「検索」と「表示済み板」の 2 項目。
                // li.board と同じ見た目にするため board クラスも付け、JS は provider-action で見分ける。
                sb.Append(@"<ul class=""boards provider-actions"">");
                if (pv.SupportsSearch)
                    sb.Append(@"<li class=""board provider-action"" data-provider=""").Append(HtmlEscape.Attr(pv.Id))
                      .Append(@""" data-action=""search"">🔍 検索</li>");
                sb.Append(@"<li class=""board provider-action"" data-provider=""").Append(HtmlEscape.Attr(pv.Id))
                  .Append(@""" data-action=""shown"">📁 表示済み板</li>");
                sb.Append("</ul>");
            }
            else if (own.Count == 0)
            {
                // 提供者固有の案内 (reddit の「ログインすると購読一覧を取得できます」等) があればそれを出す
                var hint = ChBrowser.Services.Bbs.BbsRegistry.FindById(pv.Id)?.BoardListEmptyHint;
                if (!string.IsNullOrEmpty(hint))
                    sb.Append(@"<div class=""provider-empty"">").Append(HtmlEscape.Text(hint)).Append("</div>");
                else
                    sb.Append(@"<div class=""provider-empty"">未取得 (メニューの 板一覧 → ")
                      .Append(HtmlEscape.Text(pv.DisplayName)).Append("板一覧更新)</div>");
            }
            else
                sb.Append(BuildCategories(own));
            sb.Append("</details>");
        }
        return LoadShellHtml().Replace("<!--{{ITEMS}}-->", sb.ToString());
    }

    private static int CountBoards(IReadOnlyList<BoardCategoryViewModel> categories)
    {
        var n = 0;
        foreach (var c in categories) n += c.Boards.Count;
        return n;
    }

    private static string BuildCategories(IReadOnlyList<BoardCategoryViewModel> categories)
    {
        var sb = new StringBuilder(8192);
        foreach (var cat in categories)
        {
            sb.Append(@"<details class=""category""");
            if (cat.IsExpanded) sb.Append(@" open");
            sb.Append(@" data-category=""").Append(HtmlEscape.Attr(cat.CategoryName)).Append('"');
            sb.Append(@" data-provider=""").Append(HtmlEscape.Attr(cat.ProviderId)).Append('"');
            sb.Append('>');

            sb.Append(@"<summary class=""category-name"">").Append(HtmlEscape.Text(cat.CategoryName)).Append("</summary>");
            sb.Append(@"<ul class=""boards"">");
            foreach (var bvm in cat.Boards)
            {
                var b = bvm.Board;
                sb.Append(@"<li class=""board""");
                sb.Append(@" data-host=""").Append(HtmlEscape.Attr(b.Host)).Append('"');
                sb.Append(@" data-dir=""").Append(HtmlEscape.Attr(b.DirectoryName)).Append('"');
                sb.Append(@" data-name=""").Append(HtmlEscape.Attr(b.BoardName)).Append('"');
                sb.Append('>');
                sb.Append(HtmlEscape.Text(b.BoardName));
                sb.Append("</li>");
            }
            sb.Append("</ul>");
            sb.Append("</details>");
        }
        return sb.ToString();
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
