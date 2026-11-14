using System.Windows.Controls;

namespace ChBrowser.Views.Settings;

/// <summary>「サムネイルルール」カテゴリのパネル (サムネイルルールの一覧・編集・テスト)。
/// 処理は <see cref="ChBrowser.ViewModels.SettingsViewModel"/> (SettingsViewModel.Thumbnails.cs) にある。</summary>
public partial class ThumbnailRulesPanel : UserControl
{
    public ThumbnailRulesPanel()
    {
        InitializeComponent();
    }
}
