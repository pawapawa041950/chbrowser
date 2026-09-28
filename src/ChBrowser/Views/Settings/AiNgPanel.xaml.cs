using System.Windows.Controls;

namespace ChBrowser.Views.Settings;

/// <summary>「AI NG」カテゴリのパネル (NG 判定 AI の ON / OFF・使う LLM プロファイル・同時実行数・リーズニング OFF)。
/// 接続先 (URL・キー等) は「LLM」カテゴリのプロファイルに移したので、このパネルは選択だけ (コードは不要)。</summary>
public partial class AiNgPanel : UserControl
{
    public AiNgPanel()
    {
        InitializeComponent();
    }
}
