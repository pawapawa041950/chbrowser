using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ChBrowser.ViewModels;

namespace ChBrowser.Views.Panes;

/// <summary>「上ボタン」バー (= Chrome ブックマークバー風、お気に入り直下「上ボタン」フォルダの中身を表示)。
///
/// <para>MainViewModel.TopButtonsItems の変更を購読して <see cref="BarMenu"/> の MenuItem を毎回再構築する。
/// folder → 子を持つ親 MenuItem、leaf (board/thread) → Click で MainViewModel.OpenFavoriteByIdAsync を呼ぶ。
/// アイコンは付けず、ヘッダ文字のみで簡素に。</para></summary>
public partial class TopButtonsPane : UserControl
{
    private MainViewModel? _vm;

    public TopButtonsPane()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.TopButtonsItems.CollectionChanged -= OnItemsChanged;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }
        _vm = e.NewValue as MainViewModel;
        if (_vm is not null)
        {
            _vm.TopButtonsItems.CollectionChanged += OnItemsChanged;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
        Rebuild();
    }

    /// <summary>個別エントリの DisplayName 変化 (= フォルダ名変更 / スレタイトル更新等) でも再描画したいので、
    /// 念のため Favorites 全体の Changed 経路を経由させる。実体は MainViewModel.RefreshTopButtons が
    /// TopButtonsItems を再構築するので、Collection の Reset を契機に <see cref="Rebuild"/> が走る。</summary>
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 何もしない (CollectionChanged 経路で十分)。将来別フィールドを足す際の hook 用に残す。
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        BarMenu.Items.Clear();
        if (_vm is null) return;
        foreach (var entry in _vm.TopButtonsItems)
            BarMenu.Items.Add(BuildMenuItem(entry));
    }

    /// <summary>FavoriteEntryViewModel から MenuItem を再帰的に組み立てる。
    /// folder → 子を再帰生成して詰める。leaf → Click でお気に入り起動。</summary>
    private MenuItem BuildMenuItem(FavoriteEntryViewModel entry)
    {
        var mi = new MenuItem
        {
            Header = entry.DisplayName,
            Tag    = entry.Model.Id,
        };
        if (entry is FavoriteFolderViewModel folder)
        {
            // 空フォルダでも視覚的に「フォルダ」だと分かるよう、空配列なら何もせず leaf 扱いされない
            // (MenuItem は子を持つかどうかで描画が変わる)。空のフォルダは展開しても何もないだけ。
            foreach (var child in folder.Children)
                mi.Items.Add(BuildMenuItem(child));
        }
        else
        {
            mi.Click += LeafItem_Click;
        }
        return mi;
    }

    private void LeafItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not System.Guid id) return;
        if (DataContext is not MainViewModel main) return;
        _ = main.OpenFavoriteByIdAsync(id);
    }
}
