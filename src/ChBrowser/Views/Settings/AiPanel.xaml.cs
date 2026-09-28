using System.Windows.Controls;

namespace ChBrowser.Views.Settings;

/// <summary>「AIチャット」カテゴリのパネル (AI チャットが使う LLM プロファイル・作業モデルの分割・並列実行・板説明・MCP サーバ)。
/// 接続先 (URL・キー等) は「LLM」カテゴリのプロファイルに移したので、このパネルは選択だけ (コードは不要)。</summary>
public partial class AiPanel : UserControl
{
    public AiPanel()
    {
        InitializeComponent();
    }
}
