using System.Windows.Controls;

namespace ChBrowser.Views.Settings;

/// <summary>「サムネイル取得」カテゴリのパネル (サムネイルの取得ルールの一覧・編集・テスト)。
/// 処理は <see cref="ChBrowser.ViewModels.SettingsViewModel"/> (SettingsViewModel.Thumbnails.cs) にある。</summary>
public partial class ThumbnailRulesPanel : UserControl
{
    public ThumbnailRulesPanel()
    {
        InitializeComponent();
    }
}
