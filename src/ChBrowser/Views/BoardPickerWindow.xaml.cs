using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ChBrowser.ViewModels;

namespace ChBrowser.Views;

/// <summary>NG ルールの「板名」選択用モーダルダイアログ。検索 TextBox + ListBox + OK/キャンセル。
/// 検索は DirectoryName (URL 英名) の Contains で行う。先頭の (グローバル) は検索文字が空のときのみ表示。
/// IME は無効化 (= ASCII 固定) して入力の重さと変換確定の煩雑さを避ける。</summary>
public partial class BoardPickerWindow : Window
{
    private readonly IReadOnlyList<BoardScopeViewModel>     _all;
    private readonly ObservableCollection<BoardScopeViewModel> _filtered = new();

    /// <summary>OK で確定された scope。キャンセル時は null。</summary>
    public BoardScopeViewModel? PickedScope { get; private set; }

    public BoardPickerWindow(IReadOnlyList<BoardScopeViewModel> available, BoardScopeViewModel? current)
    {
        InitializeComponent();
        _all                 = available;
        ResultList.ItemsSource = _filtered;
        ApplyFilter("");

        if (current is not null)
        {
            ResultList.SelectedItem = current;
            // 初期表示で現在の選択にスクロール
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ResultList.SelectedItem is not null)
                    ResultList.ScrollIntoView(ResultList.SelectedItem);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        Loaded += (_, _) => SearchBox.Focus();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        => ApplyFilter(SearchBox.Text);

    private void ApplyFilter(string text)
    {
        // 現在の選択を保持して、フィルタ後も同じ scope を選択し直せるよう試みる
        var prev = ResultList.SelectedItem as BoardScopeViewModel;

        _filtered.Clear();
        var trimmed = (text ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            foreach (var s in _all) _filtered.Add(s);
        }
        else
        {
            foreach (var s in _all)
            {
                // (グローバル) は検索対象外 (= text が空のときだけ出す)
                if (string.IsNullOrEmpty(s.DirectoryName)) continue;
                if (s.DirectoryName.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
                    _filtered.Add(s);
            }
        }

        if (prev is not null && _filtered.Contains(prev))
            ResultList.SelectedItem = prev;
        else if (_filtered.Count > 0)
            ResultList.SelectedIndex = 0;
    }

    /// <summary>検索ボックスから矢印キーでリスト選択を動かせるようにする。
    /// Enter は IsDefault="True" の OK ボタンが拾うので、ここでは扱わない。</summary>
    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_filtered.Count == 0) return;

        if (e.Key == Key.Down)
        {
            var idx = ResultList.SelectedIndex;
            ResultList.SelectedIndex = idx < _filtered.Count - 1 ? idx + 1 : 0;
            ResultList.ScrollIntoView(ResultList.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            var idx = ResultList.SelectedIndex;
            ResultList.SelectedIndex = idx > 0 ? idx - 1 : _filtered.Count - 1;
            ResultList.ScrollIntoView(ResultList.SelectedItem);
            e.Handled = true;
        }
    }

    private void ResultList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => OkButton.IsEnabled = ResultList.SelectedItem is BoardScopeViewModel;

    private void ResultList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Commit();

    private void OkButton_Click(object sender, RoutedEventArgs e) => Commit();

    private void Commit()
    {
        if (ResultList.SelectedItem is not BoardScopeViewModel s) return;
        PickedScope  = s;
        DialogResult = true;
    }
}
