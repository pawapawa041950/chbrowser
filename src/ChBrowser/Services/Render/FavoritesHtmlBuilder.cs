using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
        foreach (var vm in roots) AppendEntry(sb, vm);
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
        sb.Append(@"<span class=""label"">").Append(HtmlEscape.Text(t.Title)).Append("</span>");
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
            var asm  = typeof(FavoritesHtmlBuilder).Assembly;
            var html = ReadEmbeddedText(asm, "ChBrowser.Resources.favorites.html");
            // Phase 11d: ThemeService 経由で disk-first CSS を取得
            var css  = ChBrowser.Services.Theme.ThemeService.CurrentInstance?.LoadCss("favorites.css")
                       ?? ReadEmbeddedText(asm, "ChBrowser.Resources.favorites.css");
            var js   = ReadEmbeddedText(asm, "ChBrowser.Resources.favorites.js");
            _shellHtmlCache = html
                .Replace("/*{{CSS}}*/", css)
                .Replace("/*{{JS}}*/",  js);
            return _shellHtmlCache;
        }
    }

    private static string ReadEmbeddedText(Assembly asm, string name)
    {
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException($"Embedded resource not found: {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
