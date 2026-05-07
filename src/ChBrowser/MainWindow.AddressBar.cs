using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ChBrowser.ViewModels;
using Microsoft.Web.WebView2.Wpf;

namespace ChBrowser;

/// <summary>アドレスバー (Phase 14) のフォーカス / クリック / Enter / Esc / 貼り付け移動の挙動。</summary>
public partial class MainWindow
{
    /// <summary>外部 (= ShortcutManager) から呼ばれる、アドレスバーへフォーカス + 全選択。</summary>
    public void FocusAddressBar()
    {
        AddressBar.Focus();
        AddressBar.SelectAll();
    }

    /// <summary>VM の AddressBarUrl / IsLogPaneVisible が変わったときの bridge。
    /// AddressBarUrl は TextBox.Text に同期 (ユーザ編集中 = IsKeyboardFocusWithin のときは保留)。
    /// IsLogPaneVisible は別ウィンドウ (LogWindow) の show / hide に反映する。</summary>
    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null) return;
        if (e.PropertyName == nameof(MainViewModel.AddressBarUrl))
        {
            if (!AddressBar.IsKeyboardFocusWithin)
                AddressBar.Text = _vm.AddressBarUrl;
            return;
        }
        if (e.PropertyName == nameof(MainViewModel.IsLogPaneVisible))
        {
            ApplyLogWindowVisibility(_vm.IsLogPaneVisible);
            return;
        }
    }

    /// <summary>アドレスバー: フォーカス取得時に全選択。Ctrl+L / Tab / クリック (= 別ハンドラ経由) すべてで効く。</summary>
    private void AddressBar_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => AddressBar.SelectAll();

    /// <summary>未フォーカスの状態でクリックした最初の 1 回は全選択にする。
    /// 既にフォーカスがある状態でのクリックは通常通り (= キャレット位置決め) を許容。</summary>
    private void AddressBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!AddressBar.IsKeyboardFocusWithin)
        {
            AddressBar.Focus();
            e.Handled = true;
        }
    }

    /// <summary>Enter で URL ナビゲート、Esc で入力破棄して現在のタブ URL に戻す。</summary>
    private async void AddressBar_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await NavigateAddressBarAndResyncAsync(AddressBar.Text);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_vm is not null) AddressBar.Text = _vm.AddressBarUrl;
            AddressBar.SelectAll();
        }
    }

    /// <summary>VM へナビゲート要求を投げ、完了後に AddressBar.Text を「現在のタブ URL」に同期。</summary>
    private async Task NavigateAddressBarAndResyncAsync(string text)
    {
        if (_vm is null) return;
        await _vm.NavigateAddressBarAsync(text);
        if (!_vm.AddressBarHasError) AddressBar.Text = _vm.AddressBarUrl;
        AddressBar.SelectAll();
    }

    /// <summary>アドレスバーから他へフォーカスが移った時、移動先が WebView2 (= 板/スレ表示) なら入力テキストを破棄し
    /// 現在のタブ URL に戻す。それ以外 (メニュー / 設定ウィンドウ等) なら入力テキストを保持し続ける。</summary>
    private void AddressBar_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_vm is null) return;
        if (IsInsideWebView(e.NewFocus as DependencyObject))
            AddressBar.Text = _vm.AddressBarUrl;
    }

    private static bool IsInsideWebView(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is WebView2) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    /// <summary>右クリックメニュー「貼り付けて移動」: クリップボード文字列を AddressBar に入れて即ナビゲート。</summary>
    private async void AddressBarPasteAndGo_Click(object sender, RoutedEventArgs e)
    {
        var text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        if (string.IsNullOrWhiteSpace(text)) return;
        AddressBar.Text = text;
        await NavigateAddressBarAndResyncAsync(text);
    }
}
