using System;
using ChBrowser.Services.Image;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChBrowser.ViewModels;

/// <summary>ビューアウィンドウの 1 タブ。1 画像 = 1 タブ (Phase 10)。</summary>
public sealed partial class ImageViewerTabViewModel : ObservableObject
{
    /// <summary>表示対象の画像 URL (HTTP/HTTPS 直リンク)。</summary>
    public string Url { get; }

    /// <summary>タブヘッダに出す短縮表示名 (URL の最終セグメントから生成)。</summary>
    public string Header { get; }

    /// <summary>このタブが現在 TabControl で選択されているか。各タブが専属 WebView2 を持ち、
    /// Visibility をこれに bind する (= 選択タブだけ可視、他は Collapsed)。
    /// スレ表示タブと同じ per-tab WebView2 パターン。</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>タブヘッダに表示するサムネイル画像のパス。
    /// 初期値は <see cref="Url"/> (= WPF Image が HTTP で fetch する) だが、
    /// <see cref="RefreshThumbnailFromCache"/> でキャッシュにあれば file path に切替えて
    /// 特殊ヘッダ要求 (pixiv の Referer 等) を回避できるようにする。</summary>
    [ObservableProperty]
    private string _thumbnailPath;

    public IRelayCommand CloseCommand { get; }

    public ImageViewerTabViewModel(string url, Action<ImageViewerTabViewModel> closeCallback)
    {
        Url            = url;
        Header         = ShortNameFromUrl(url);
        _thumbnailPath = url;          // 初期は URL 直接 (= WPF が HTTP で取りにいく)
        CloseCommand   = new RelayCommand(() => closeCallback(this));
    }

    /// <summary>キャッシュに <see cref="Url"/> がヒットしたら、
    /// <see cref="ThumbnailPath"/> をローカルファイルパスに切り替える。
    /// ビューア WebView2 が画像をロード完了した直後に呼ばれる。</summary>
    public void RefreshThumbnailFromCache(ImageCacheService cache)
    {
        if (cache.TryGet(Url, out var hit))
        {
            ThumbnailPath = hit.FilePath;
        }
    }

    /// <summary>URL の末尾セグメント (= ファイル名) を取って先頭 24 文字に切り詰める。
    /// クエリ付きでも file.jpg?abc=1 → file.jpg として扱う。</summary>
    private static string ShortNameFromUrl(string url)
    {
        try
        {
            var uri  = new Uri(url, UriKind.Absolute);
            var seg  = uri.Segments;
            var last = seg.Length > 0 ? seg[^1].TrimEnd('/') : url;
            if (string.IsNullOrEmpty(last)) last = uri.Host;
            const int max = 24;
            return last.Length <= max ? last : last[..max] + "…";
        }
        catch
        {
            return url.Length <= 24 ? url : url[..24] + "…";
        }
    }
}
