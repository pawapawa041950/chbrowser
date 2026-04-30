using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ChBrowser.Services.Image;
using ChBrowser.ViewModels;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace ChBrowser.Views;

/// <summary>
/// 画像ビューアウィンドウ (Phase 10)。アプリ全体でシングルトン (App.xaml.cs で 1 度だけ生成)。
/// DataContext は <see cref="ImageViewerViewModel"/>。
/// </summary>
public partial class ImageViewerWindow : Window
{
    private readonly ImageSaver        _imageSaver;
    private readonly ImageCacheService _imageCache;

    public ImageViewerWindow(ImageViewerViewModel vm, ImageSaver imageSaver, ImageCacheService imageCache)
    {
        InitializeComponent();
        DataContext  = vm;
        _imageSaver  = imageSaver;
        _imageCache  = imageCache;

        // VM のキーボードショートカットからもメニューと同じ動作を実行できるように Window 側のロジックを配線
        vm.SaveImageRequested     = url => SaveImageWithDialog(url);
        vm.CopyUrlRequested       = url => CopyUrl(url);
        vm.OpenInBrowserRequested = url => OpenInBrowser(url);

        // 「✕」で閉じても破棄せず、隠してアプリ常駐させる (= 次に画像を開く時は再表示)
        Closing += (_, e) =>
        {
            if (Application.Current?.MainWindow is not null && !App.IsShuttingDown)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    /// <summary>JS の viewer.js から飛んでくるメッセージを捌く。</summary>
    private void ViewerWebView_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (DataContext is not ImageViewerViewModel vm) return;

        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var tp) ? tp.GetString() : null;

            // Phase 16+: ブリッジから shortcut / gesture / bridgeReady / gestureProgress を受信。
            // category は "ビューアウィンドウ" 固定。
            if (type == "shortcut" || type == "gesture")
            {
                if (Application.Current is App app && app.ShortcutManager is { } mgr)
                {
                    var descriptor = root.TryGetProperty("descriptor", out var dp) ? dp.GetString() : null;
                    if (!string.IsNullOrEmpty(descriptor)) mgr.Dispatch("ビューアウィンドウ", descriptor);
                }
                return;
            }
            if (type == "gestureProgress" || type == "gestureEnd")
            {
                if (Application.Current is App app && app.ShortcutManager is { } mgr)
                {
                    if (type == "gestureEnd") mgr.NotifyGestureProgress(null, null);
                    else
                    {
                        var value = root.TryGetProperty("value", out var vp) ? vp.GetString() : "";
                        mgr.NotifyGestureProgress("ビューアウィンドウ", value ?? "");
                    }
                }
                return;
            }
            if (type == "bridgeReady")
            {
                if (Application.Current is App app && app.ShortcutManager is { } mgr
                    && sender is Microsoft.Web.WebView2.Wpf.WebView2 wv && wv.CoreWebView2 is { } core)
                {
                    var map = mgr.GetBindingsForCategory("ビューアウィンドウ");
                    var json = JsonSerializer.Serialize(new { type = "setShortcutBindings", bindings = map });
                    try { core.PostWebMessageAsJson(json); } catch (Exception ex) { Debug.WriteLine($"[ImageViewer] PushBindings failed: {ex.Message}"); }
                }
                return;
            }

            switch (type)
            {
                case "wheelTab":
                {
                    var dir = root.TryGetProperty("direction", out var dp) ? dp.GetString() : null;
                    if (dir == "next") vm.NextTab();
                    else               vm.PrevTab();
                    break;
                }
                case "contextMenu":
                {
                    if (vm.SelectedTab is { Url: var url } && !string.IsNullOrEmpty(url))
                        ShowImageContextMenu(url);
                    break;
                }
                case "imageReady":
                {
                    // ビューア WebView2 が画像ロード完了 → キャッシュ書き込みも (ほぼ) 完了している。
                    // タブのサムネイルを HTTP URL からキャッシュ file path に切り替えて、
                    // 特殊ヘッダ要求 (pixiv の Referer 等) を回避する。
                    if (sender is Microsoft.Web.WebView2.Wpf.WebView2 wv
                        && wv.DataContext is ImageViewerTabViewModel tab)
                    {
                        tab.RefreshThumbnailFromCache(_imageCache);
                    }
                    break;
                }
                // imageError は今のところ無視 (status 表示は Phase 10c 以降で検討)
            }
        }
        catch (JsonException) { /* malformed message — ignore */ }
    }

    /// <summary>新規にタブを開いてウィンドウを Show + 前面化する。</summary>
    public void OpenAndShow(string url)
    {
        if (DataContext is not ImageViewerViewModel vm) return;
        var tab = vm.OpenOrAddTab(url);
        // 既にキャッシュにあれば即座にサムネをローカルファイルに切替 (HTTP fetch を避ける)
        tab.RefreshThumbnailFromCache(_imageCache);
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Window.Resources の "ImageContextMenu" を取り出して URL を Tag に積んで popup。</summary>
    private void ShowImageContextMenu(string url)
    {
        if (Resources["ImageContextMenu"] is not ContextMenu menu) return;
        menu.Tag             = url;
        menu.PlacementTarget = this;
        menu.Placement       = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen          = true;
    }

    /// <summary>ContextMenu.Tag から URL を取り出すヘルパ (各クリックハンドラで共通)。</summary>
    private static string? UrlFromMenu(object sender)
    {
        if (sender is not MenuItem mi) return null;
        var owner = ItemsControl.ItemsControlFromItemContainer(mi) as ContextMenu
                  ?? mi.Parent as ContextMenu;
        return owner?.Tag as string;
    }

    private async void SaveImage_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlFromMenu(sender);
        if (string.IsNullOrEmpty(url)) return;
        await SaveImageWithDialog(url);
    }

    private void CopyImageUrl_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlFromMenu(sender);
        if (string.IsNullOrEmpty(url)) return;
        CopyUrl(url);
    }

    private void OpenInBrowser_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlFromMenu(sender);
        if (string.IsNullOrEmpty(url)) return;
        OpenInBrowser(url);
    }

    /// <summary>ContextMenu / Ctrl+S の両方から呼ばれる。SaveFileDialog を出して保存する。</summary>
    private async System.Threading.Tasks.Task SaveImageWithDialog(string url)
    {
        var dlg = new SaveFileDialog
        {
            FileName  = ImageSaver.SuggestFileName(url),
            Filter    = "画像ファイル (*.jpg;*.png;*.gif;*.webp)|*.jpg;*.png;*.gif;*.webp|すべてのファイル (*.*)|*.*",
            Title     = "画像を保存",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            await _imageSaver.SaveAsync(url, dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存に失敗しました: {ex.Message}", "ChBrowser",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CopyUrl(string url)
    {
        try { Clipboard.SetText(url); }
        catch (Exception ex) { Debug.WriteLine($"[ImageViewer] CopyUrl failed: {ex.Message}"); }
    }

    private static void OpenInBrowser(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = uri.ToString(),
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ImageViewer] OpenInBrowser failed: {ex.Message}");
        }
    }
}
