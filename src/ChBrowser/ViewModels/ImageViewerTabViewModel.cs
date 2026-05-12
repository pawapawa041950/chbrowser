using System;
using System.Text.RegularExpressions;
using ChBrowser.Services.Image;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChBrowser.ViewModels;

/// <summary>ビューアにズーム/フィット要求を送るためのラッパー。
/// <see cref="ImageViewerTabViewModel.PendingZoomMode"/> に新インスタンスを setter するたび
/// AttachedProperty 経由で JS に setZoom メッセージが飛ぶ。
/// <para>Mode は "actual" (= 1:1 ピクセル表示) または "fit" (= ウィンドウに収める) の 2 種類。</para>
/// <para>注意: <b>record にしないこと</b>。record は構造的等価性で setter が短絡し、同じ Mode を 2 回連続で
/// 投げると 2 回目が PropertyChanged を発火させない (= 「初回しか効かない」現象になる)。class で参照同一性にして
/// 毎回必ず発火させる。</para></summary>
public sealed class ZoomModeRequest
{
    public string Mode { get; }
    public ZoomModeRequest(string mode) => Mode = mode;
}

/// <summary>ビューアウィンドウの 1 タブ。1 画像 = 1 タブ (Phase 10)。</summary>
public sealed partial class ImageViewerTabViewModel : ObservableObject
{
    /// <summary>表示対象の画像 / 動画 URL (HTTP/HTTPS 直リンク)。</summary>
    public string Url { get; }

    /// <summary>このタブのコンテンツが動画 (= mp4/webm/mov) か。
    /// true なら HTTP fetch 系のサムネ取得 / 画像キャッシュ参照をスキップして
    /// 動画用プレースホルダ (再生アイコン) をタブヘッダに表示する。</summary>
    public bool IsVideo { get; }

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

    /// <summary>viewer の <c>&lt;video&gt;</c> / <c>&lt;img&gt;</c> に実際に渡す再生用 URL。
    /// デフォルトは <see cref="Url"/> (原 URL)。
    /// 動画タブで <see cref="InitializeForVideo"/> が動画本体キャッシュヒットを確認した場合、
    /// 仮想ホスト URL (<c>https://chbrowser-cache.local/videos/...</c>) に差し替える
    /// (= viewer はローカルから即時再生、シーク自由)。
    /// XAML は <c>WebView2Helper.ImageUrl="{Binding PlaybackUrl}"</c> でこれにバインドする。</summary>
    [ObservableProperty]
    private string _playbackUrl;

    /// <summary>JS にズーム/フィット指示を push するトリガ。値が変わると WebView2Helper.ZoomModePush で送信される。
    /// 右クリックメニュー「画像を原寸表示」「ウィンドウに合わせる」から setter される。</summary>
    [ObservableProperty]
    private ZoomModeRequest? _pendingZoomMode;

    public IRelayCommand CloseCommand { get; }

    /// <summary>動画 URL (.mp4 / .webm / .mov) 判定用。viewer.js 側の VIDEO_EXT_RE と意図的に同パターン。</summary>
    private static readonly Regex VideoExtRegex = new(@"\.(mp4|webm|mov)(?:[?#]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ImageViewerTabViewModel(string url, Action<ImageViewerTabViewModel> closeCallback)
    {
        Url            = url;
        IsVideo        = VideoExtRegex.IsMatch(url);
        Header         = ShortNameFromUrl(url);
        // 画像は URL 直接 (= WPF が HTTP fetch) → ロード完了で cache file path に差替。
        // 動画は HTTP fetch しても画像にならないので空文字。
        // 動画タブは InitializeForVideo() でキャッシュをチェック → サムネ仮想ホスト URL に差替。
        _thumbnailPath = IsVideo ? "" : url;
        // 再生用 URL の既定は原 URL。動画タブは InitializeForVideo() でキャッシュ済なら仮想ホスト URL に差替。
        _playbackUrl   = url;
        CloseCommand   = new RelayCommand(() => closeCallback(this));
    }

    /// <summary>動画タブ専用の初期化 (Phase 6)。
    /// <list type="bullet">
    /// <item><description>VideoThumb キャッシュヒット → <see cref="ThumbnailPath"/> に仮想ホスト URL をセット</description></item>
    /// <item><description>Video キャッシュヒット → <see cref="PlaybackUrl"/> に仮想ホスト URL をセット (= 即時ローカル再生)</description></item>
    /// <item><description>Video キャッシュミス → <see cref="VideoDownloadManager.Request"/> で並列 DL を kick (= 次回からキャッシュヒット)</description></item>
    /// </list>
    /// 画像タブには何もしない (= IsVideo=false なら no-op)。</summary>
    public void InitializeForVideo(ImageCacheService cache, ChBrowser.Services.Media.VideoDownloadManager dlManager)
    {
        if (!IsVideo) return;

        if (cache.TryGet(Url, out var thumbHit, CacheKind.VideoThumb))
        {
            // ThumbnailPath は WPF <Image Source> に bind されるため、
            // WebView2 だけが解決できる仮想ホスト URL ではなくローカル file path を渡す必要がある。
            ThumbnailPath = thumbHit.FilePath;
        }
        if (cache.TryGet(Url, out var videoHit, CacheKind.Video))
        {
            // PlaybackUrl は viewer 内 (= WebView2) の <video src> に渡るので仮想ホスト URL で OK。
            PlaybackUrl = cache.BuildVirtualHostUrl(videoHit);
        }
        else
        {
            // 未キャッシュ動画 → ストリーミング再生と並行して C# 側で並列 DL を kick (Phase 4)。
            // 次回同 URL を viewer / thread で開いた時はキャッシュヒットで即時ローカル再生になる。
            dlManager.Request(Url);
        }
    }

    /// <summary>キャッシュに <see cref="Url"/> がヒットしたら、
    /// <see cref="ThumbnailPath"/> をローカルファイルパスに切り替える。
    /// ビューア WebView2 が画像をロード完了した直後に呼ばれる。
    /// 動画タブは InitializeForVideo の方で扱うので何もしない。</summary>
    public void RefreshThumbnailFromCache(ImageCacheService cache)
    {
        if (IsVideo) return;
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
