using System.Collections.Generic;
using System.IO;
using System.Reflection;
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

    public static string Build(IReadOnlyList<BoardCategoryViewModel> categories)
    {
        var sb = new StringBuilder(8192);
        foreach (var cat in categories)
        {
            sb.Append(@"<details class=""category""");
            if (cat.IsExpanded) sb.Append(@" open");
            sb.Append(@" data-category=""").Append(HtmlEscape.Attr(cat.CategoryName)).Append('"');
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

        return LoadShellHtml().Replace("<!--{{ITEMS}}-->", sb.ToString());
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
            var asm    = typeof(BoardListHtmlBuilder).Assembly;
            var html   = ReadEmbeddedText(asm, "ChBrowser.Resources.board-list.html");
            // Phase 11d: ThemeService 経由で disk-first CSS を取得
            var css    = ChBrowser.Services.Theme.ThemeService.CurrentInstance?.LoadCss("board-list.css")
                         ?? ReadEmbeddedText(asm, "ChBrowser.Resources.board-list.css");
            var js     = ReadEmbeddedText(asm, "ChBrowser.Resources.board-list.js");
            var bridge = ReadEmbeddedText(asm, "ChBrowser.Resources.shortcut-bridge.js");
            _shellHtmlCache = html
                .Replace("/*{{CSS}}*/",             css)
                .Replace("/*{{SHORTCUT_BRIDGE}}*/", bridge)
                .Replace("/*{{JS}}*/",              js);
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
