using System;
using System.IO;
using System.Text;

namespace ChBrowser.Services.Storage;

/// <summary>「AI 向けの各板/スレの使い分け説明」をユーザが自由に編集するテキストファイルの入出力。
/// <c>data/app/ai-board-guide.txt</c>。AI チャットを開くたびに読み込まれ、Strategist / Worker の文脈に
/// 注入される (= 関連スレ探索時の板選びの精度を上げる)。設定ウィンドウ「AI」カテゴリの
/// 「説明テキストを開く」ボタンから関連付けエディタで編集する。</summary>
public sealed class AiBoardGuideStorage
{
    private readonly DataPaths _paths;
    public AiBoardGuideStorage(DataPaths paths) => _paths = paths;

    public string FilePath => _paths.AiBoardGuidePath;

    /// <summary>初回 / 未作成時に書き込む既定の説明文 (= 開発者が普段使う板の使い分け)。</summary>
    public const string Default =
        "（このファイルは「板やスレの使い分け」を AI に教えるためのメモです。AI が関連スレを探すときの板選びに使います。自由に編集してください。）\n" +
        "\n" +
        "* 時事情報をもとに雑談するのが「ニュース速報(嫌儲)」。災害時の情報もここが早い\n" +
        "* スポーツや芸能のニュースをもとに雑談するのが「芸スポ速報+」\n" +
        "* 放映中のアニメは「アニメ」\n" +
        "* 放映終了後のアニメは「アニメ2」\n" +
        "* 放映終了後5年以上たったものは「懐かしアニメ平成」、「懐かしアニメ昭和」\n" +
        "* アニメキャラの話題「アニメキャラ(個別)」\n" +
        "* 生成AIの最新情報については「なんでも実況U」の「なんJNVA部」(画像生成)、「なんJLLM部」(LLM)に集まりやすい\n" +
        "* 漫画は「週刊少年漫画」、「少年漫画」(週刊ではない少年漫画)、「漫画」(前者に含まれない漫画や青年漫画など)が中心だが、作品ごとにどの板にスレが建てるかの判断基準はかなりばらつきがあり\n";

    /// <summary>ファイルが無ければ既定文で作成する (= UTF-8 BOM なし)。</summary>
    public void EnsureExists()
    {
        try
        {
            if (!File.Exists(FilePath))
                File.WriteAllText(FilePath, Default, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            ChBrowser.Services.Logging.LogService.Instance.Write($"[AiBoardGuide] 既定生成に失敗: {ex.Message}");
        }
    }

    /// <summary>現在の説明文を読み込む。未作成なら既定を作って返す。読めなければ既定文字列。</summary>
    public string Load()
    {
        try
        {
            EnsureExists();
            return File.ReadAllText(FilePath);
        }
        catch
        {
            return Default;
        }
    }
}
