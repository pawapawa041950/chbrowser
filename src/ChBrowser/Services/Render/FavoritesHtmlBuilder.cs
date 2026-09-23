using System.Collections.Generic;
using System.Text;
using ChBrowser.ViewModels;

namespace ChBrowser.Services.Render;

/// <summary>
/// お気に入りペイン (Phase 14b)。ページ (シェル) は 1 回だけ読み込み、ツリーは JSON で送って JS が組み立てる
/// (<c>setFavorites</c> メッセージ。板一覧・スレ一覧と同じ「シェル + データ push」方式)。
/// お気に入りを追加・削除・移動してもページを読み直さないので、スクロール位置・選択・絞り込みがそのまま残る。
///
/// 階層: フォルダは <c>&lt;details&gt;/&lt;summary&gt;</c>、板/スレは <c>&lt;li&gt;</c> (JS 側で組む)。
/// 全エントリに <c>data-id</c> (Guid) と <c>data-type</c> (folder/board/thread) を付与し、
/// JS のイベントハンドラ (dblclick / contextmenu / dragstart / drop) はその id を C# に postMessage で送る。
/// </summary>
public static class FavoritesHtmlBuilder
{
    private static string? _shellHtmlCache;
    private static readonly object Lock = new();

    /// <summary>お気に入りペインのシェル HTML (中身は空。JS が <c>ready</c> を送り、C# が <c>setFavorites</c> で中身を送る)。</summary>
    public static string BuildShell() => LoadShellHtml();

    /// <summary>お気に入りツリーの送信データ (「お気に入り」仮想ルートの子の並び)。</summary>
    public static IReadOnlyList<FavoriteNode> BuildTree(IReadOnlyList<FavoriteEntryViewModel> roots)
    {
        var list = new List<FavoriteNode>(roots.Count);
        foreach (var vm in roots) if (ToNode(vm) is { } n) list.Add(n);
        return list;
    }

    private static FavoriteNode? ToNode(FavoriteEntryViewModel vm) => vm switch
    {
        FavoriteFolderViewModel f => new FavoriteNode("folder", f.Model.Id.ToString(), f.DisplayName, f.IsExpanded,
                                         Children: BuildTree(f.Children)),
        FavoriteBoardViewModel  b => new FavoriteNode("board", b.Model.Id.ToString(), b.Model.BoardName,
                                         Host: b.Model.Host, Dir: b.Model.DirectoryName),
        FavoriteThreadViewModel t => new FavoriteNode("thread", t.Model.Id.ToString(), t.Model.Title,
                                         Host: t.Model.Host, Dir: t.Model.DirectoryName, Key: t.Model.ThreadKey, Board: t.Model.BoardName),
        _ => null,
    };

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

/// <summary>お気に入りツリーの 1 ノード (<c>setFavorites</c> で JS へ送る)。<see cref="Type"/> = "folder" / "board" / "thread"。
/// フォルダは <see cref="Expanded"/> と <see cref="Children"/>、板・スレは所在 (<see cref="Host"/> / <see cref="Dir"/> / <see cref="Key"/>) を持つ。</summary>
public sealed record FavoriteNode(string Type, string Id, string Name, bool Expanded = false,
                                  string? Host = null, string? Dir = null, string? Key = null, string? Board = null,
                                  IReadOnlyList<FavoriteNode>? Children = null);
