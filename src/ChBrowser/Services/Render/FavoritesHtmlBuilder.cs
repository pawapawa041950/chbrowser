using System.Collections.Generic;
using System.Text;
using ChBrowser.ViewModels;

namespace ChBrowser.Services.Render;

/// <summary>
/// お気に入りペイン (Phase 14b) を 1 枚の HTML として組み立てる。
///
/// 階層: フォルダは <c>&lt;details&gt;/&lt;summary&gt;</c>、板/スレは <c>&lt;li&gt;</c>。
/// 全エントリに <c>data-id</c> (Guid) と <c>data-type</c> (folder/board/thread) を付与し、
/// JS のイベントハンドラ (dblclick / contextmenu / dragstart / drop) はその id を C# に postMessage で送る。
/// </summary>
public static class FavoritesHtmlBuilder
{
    private static string? _shellHtmlCache;
    private static readonly object Lock = new();

    public static string Build(IReadOnlyList<FavoriteEntryViewModel> roots)
    {
        var sb = new StringBuilder(4096);
        sb.Append(@"<ul class=""fav-root"">");

        // 「機能」フォルダ (Phase 18) — お気に入り仮想ルートより上の兄弟。
        // 永続化対象外 / drag 不可 / context menu なし、JS 側は data-type="function-folder" / "all-logs" で識別。
        sb.Append(@"<li class=""fav-item"" data-type=""function-folder"">")
          .Append(@"<details class=""folder"" open>")
          .Append(@"<summary class=""folder-row""><span class=""icon icon-folder""></span><span class=""label"">機能</span></summary>")
          .Append(@"<ul class=""children"">")
          .Append(@"<li class=""fav-item board-row"" data-type=""all-logs"">")
          .Append(@"<span class=""icon icon-board""></span><span class=""label"">全ログ</span>")
          .Append(@"</li>")
          .Append(@"</ul></details></li>");

        // 全エントリを「お気に入り」仮想ルートフォルダの下に入れる。
        // この li は永続化対象ではないため data-id を持たず、data-type="virtual-root" で識別する
        // (= JS 側で contextMenu / drag 不可 / 開閉のみ、C# 側は target='virtual-root' で専用メニューを出す)。
        sb.Append(@"<li class=""fav-item"" data-type=""virtual-root"">")
          .Append(@"<details class=""folder"" open>")
          .Append(@"<summary class=""folder-row""><span class=""icon icon-folder""></span><span class=""label"">お気に入り</span></summary>")
          .Append(@"<ul class=""children"">");
        foreach (var vm in roots) AppendEntry(sb, vm);
        sb.Append("</ul></details></li>");

        sb.Append("</ul>");
        return LoadShellHtml().Replace("<!--{{ITEMS}}-->", sb.ToString());
    }

    private static void AppendEntry(StringBuilder sb, FavoriteEntryViewModel vm)
    {
        switch (vm)
        {
            case FavoriteFolderViewModel folder: AppendFolder(sb, folder); break;
            case FavoriteBoardViewModel  board:  AppendBoard (sb, board);  break;
            case FavoriteThreadViewModel thread: AppendThread(sb, thread); break;
        }
    }

    private static void AppendFolder(StringBuilder sb, FavoriteFolderViewModel folder)
    {
        sb.Append(@"<li class=""fav-item"" data-type=""folder"" data-id=""")
          .Append(folder.Model.Id).Append('"').Append(@" draggable=""true""")
          .Append('>');

        sb.Append(@"<details class=""folder""");
        if (folder.IsExpanded) sb.Append(@" open");
        sb.Append('>');

        sb.Append(@"<summary class=""folder-row""><span class=""icon icon-folder""></span><span class=""label"">")
          .Append(HtmlEscape.Text(folder.DisplayName)).Append("</span></summary>");

        sb.Append(@"<ul class=""children"">");
        foreach (var c in folder.Children) AppendEntry(sb, c);
        sb.Append("</ul>");

        sb.Append("</details>");
        sb.Append("</li>");
    }

    private static void AppendBoard(StringBuilder sb, FavoriteBoardViewModel bvm)
    {
        var b = bvm.Model;
        sb.Append(@"<li class=""fav-item board-row"" data-type=""board"" data-id=""").Append(b.Id).Append('"');
        sb.Append(@" data-host=""").Append(HtmlEscape.Attr(b.Host)).Append('"');
        sb.Append(@" data-dir=""").Append(HtmlEscape.Attr(b.DirectoryName)).Append('"');
        sb.Append(@" data-name=""").Append(HtmlEscape.Attr(b.BoardName)).Append('"');
        sb.Append(@" draggable=""true""");
        sb.Append('>');
        sb.Append(@"<span class=""icon icon-board""></span><span class=""label"">").Append(HtmlEscape.Text(b.BoardName)).Append("</span>");
        sb.Append("</li>");
    }

    private static void AppendThread(StringBuilder sb, FavoriteThreadViewModel tvm)
    {
        var t = tvm.Model;
        sb.Append(@"<li class=""fav-item thread-row"" data-type=""thread"" data-id=""").Append(t.Id).Append('"');
        sb.Append(@" data-host=""").Append(HtmlEscape.Attr(t.Host)).Append('"');
        sb.Append(@" data-dir=""").Append(HtmlEscape.Attr(t.DirectoryName)).Append('"');
        sb.Append(@" data-key=""").Append(HtmlEscape.Attr(t.ThreadKey)).Append('"');
        sb.Append(@" data-title=""").Append(HtmlEscape.Attr(t.Title)).Append('"');
        sb.Append(@" data-board=""").Append(HtmlEscape.Attr(t.BoardName)).Append('"');
        sb.Append(@" draggable=""true""");
        sb.Append('>');
        sb.Append(@"<span class=""icon icon-thread""></span><span class=""label"">").Append(HtmlEscape.Text(t.Title)).Append("</span>");
        sb.Append("</li>");
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
            var html   = EmbeddedAssets.Read("favorites.html");
            var css    = EmbeddedAssets.ReadCss("favorites.css");  // disk-first
            var js     = EmbeddedAssets.Read("favorites.js");
            var bridge = EmbeddedAssets.Read("shortcut-bridge.js");
            _shellHtmlCache = html
                .Replace("/*{{CSS}}*/",             css)
                .Replace("/*{{SHORTCUT_BRIDGE}}*/", bridge)
                .Replace("/*{{JS}}*/",              js);
            return _shellHtmlCache;
        }
    }
}
