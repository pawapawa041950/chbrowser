using System.IO;
using System.Reflection;

namespace ChBrowser.Services.Render;

/// <summary>埋め込みリソース (HTML / CSS / JS シェル) の読込ヘルパ。
/// 全 HtmlBuilder / WebView2Helper が共通利用する。CSS は <see cref="ReadCss"/> で
/// disk-first (= <see cref="ChBrowser.Services.Theme.ThemeService.LoadCss"/>) に切り替えられる。
///
/// <para>これまで各 HtmlBuilder / WebView2Helper に同形の <c>ReadEmbeddedText</c> 関数が
/// 4 箇所コピペされていたのを集約。`ChBrowser.Resources.` プレフィクスもここで吸収。</para></summary>
internal static class EmbeddedAssets
{
    private static readonly Assembly Asm = typeof(EmbeddedAssets).Assembly;

    /// <summary>埋め込みリソース 1 つを文字列として読む (例: <c>"thread.html"</c>)。
    /// 内部で <c>ChBrowser.Resources.</c> プレフィクスを補う。</summary>
    public static string Read(string fileName)
    {
        var resourceName = "ChBrowser.Resources." + fileName;
        using var stream = Asm.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>CSS を disk-first で読む。<see cref="ChBrowser.Services.Theme.ThemeService.LoadCss"/>
    /// 経由でユーザーがディスク上で編集した内容を優先し、無ければ埋め込み既定にフォールバック。
    /// ThemeService が未生成の場合 (= 起動最初期の経路、現状到達しない想定) も埋め込みにフォールバック。</summary>
    public static string ReadCss(string fileName)
        => ChBrowser.Services.Theme.ThemeService.CurrentInstance?.LoadCss(fileName)
           ?? Read(fileName);
}
