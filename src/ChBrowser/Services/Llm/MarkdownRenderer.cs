using Markdig;

namespace ChBrowser.Services.Llm;

/// <summary>
/// LLM のストリーミング応答 (markdown と想定) を HTML に変換する。
/// AI チャットウィンドウの WebView2 シェル (ai-chat.html) はこの HTML を innerHTML に流し込むだけなので、
/// 安全性のため <see cref="MarkdownPipelineBuilder.DisableHtml"/> で raw HTML をリテラル文字列扱いにする
/// (= LLM 出力に &lt;script&gt; 等が混ざってもコードとして実行されない)。
/// </summary>
internal static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras()   // ~~打ち消し線~~ など
        .UsePipeTables()       // | 表 |
        .UseAutoLinks()        // 裸の URL を自動リンク
        .DisableHtml()         // raw HTML はリテラル文字列として出力 (= XSS 防止)
        .Build();

    /// <summary>markdown 文字列を HTML に変換する。null は空文字として扱う。</summary>
    public static string ToHtml(string? markdown)
        => Markdown.ToHtml(markdown ?? "", Pipeline);
}
