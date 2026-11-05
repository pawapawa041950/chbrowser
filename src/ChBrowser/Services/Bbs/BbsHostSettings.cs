namespace ChBrowser.Services.Bbs;

/// <summary>掲示板ごとに切り替えられる設定 (提供者のコードの外から ON/OFF したい挙動をここに集める)。
///
/// <para>今は提供者の既定値 (<see cref="IBbsProvider.DefaultHostSettings"/>) だけを使う。
/// 将来は <c>data/&lt;ルートドメイン&gt;/setting.toml</c> を読み、書かれている項目だけ既定値の上に重ねる予定
/// (<see cref="For"/> がその入口。呼び出し側は変えずに済むよう、設定はここを通して引くこと)。</para></summary>
/// <param name="MarkdownHardLineBreaks">書き込みの本文 (Markdown) の単発の改行を、行末に半角スペース 2 個を付けた強制改行にして送る。
/// Markdown では単発の改行は段落内の折り返し (表示はスペース) になり、書いたとおりに改行されないため (reddit)。
/// 変換の中身は <see cref="MarkdownLineBreaks.ToHardBreaks"/>。</param>
public sealed record BbsHostSettings(
    bool MarkdownHardLineBreaks = false)
{
    /// <summary>どの掲示板も特別扱いしない既定値。</summary>
    public static BbsHostSettings Default { get; } = new();

    /// <summary>ホストの掲示板の設定。今は提供者の既定値をそのまま返す (setting.toml の読み込みは未実装)。</summary>
    public static BbsHostSettings For(string host)
        => BbsRegistry.ResolveOrDefault(host).DefaultHostSettings;
}
