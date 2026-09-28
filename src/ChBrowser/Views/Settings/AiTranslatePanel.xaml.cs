using System.Windows.Controls;

namespace ChBrowser.Views.Settings;

/// <summary>「AI翻訳」カテゴリのパネル (翻訳に使う LLM プロファイル・同時実行数・リーズニング OFF)。
/// 接続先 (URL・キー等) は「LLM」カテゴリのプロファイルに移したので、このパネルは選択だけ (コードは不要)。</summary>
public partial class AiTranslatePanel : UserControl
{
    public AiTranslatePanel()
    {
        InitializeComponent();
    }
}
