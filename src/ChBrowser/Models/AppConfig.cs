namespace ChBrowser.Models;

/// <summary>アプリ全体設定 (Phase 11)。<c>data/app/config.json</c> に永続化される。
/// 設定画面 (5.7) のカテゴリに対応するフィールドを持つ。
/// 既定値はコンストラクタ初期化子で指定 (ファイル不在時 / 未知キー時のフォールバック)。
/// 将来のスキーマ移行用に <see cref="Version"/> を持つ — 1 が初期版。</summary>
public sealed record AppConfig
{
    public int Version { get; init; } = 1;

    // ---- 全般 ----
    /// <summary>"Unaware" or "PerMonitorV2"。次回起動時に反映 (= 起動直後の SetProcessDpiAwarenessContext)。</summary>
    public string HiDpiMode { get; init; } = "Unaware";

    // ---- 通信 ----
    /// <summary>カスタム User-Agent。空文字なら既定 (Monazilla/1.00 ChBrowser/&lt;ver&gt;)。即時反映。</summary>
    public string UserAgentOverride { get; init; } = "";

    /// <summary>HTTP タイムアウト (秒)。次回起動時に反映 (HttpClient.Timeout は実行中変更不可のため)。</summary>
    public int TimeoutSec { get; init; } = 30;

    // ---- スレッド ----
    /// <summary>人気レス閾値 (= 被アンカー数 ≥ この値で赤マーカー)。即時反映 (setConfig メッセージ)。</summary>
    public int PopularThreshold { get; init; } = 3;

    /// <summary>新規スレタブ生成時のデフォルト表示モード (Flat / Tree / DedupTree)。</summary>
    public string DefaultThreadViewMode { get; init; } = "DedupTree";

    /// <summary>画像 HEAD 自動取得しきい値 (MB)。これ以上の画像はクリックされるまで取得しない。即時反映。</summary>
    public int ImageSizeThresholdMb { get; init; } = 5;

    // ---- 画像 ----
    /// <summary>画像キャッシュ上限 (MB)。即時反映 (ImageCacheService.MaxBytes)。</summary>
    public int CacheMaxMb { get; init; } = 1024;

    // ---- ビューア ----
    /// <summary>ビューアウィンドウのタブサムネイルサイズ (px、正方形)。即時反映 (ImageViewerWindow XAML binding)。</summary>
    public int ViewerThumbnailSize { get; init; } = 80;

    // ---- ペイン操作 (Phase 11b) ----
    /// <summary>お気に入りペインで 1 クリックで開く。OFF でダブルクリック動作。即時反映 (favorites.js への setConfig)。</summary>
    public bool FavoritesOpenOnSingleClick { get; init; } = true;

    /// <summary>板一覧ペインで 1 クリックで開く。OFF でダブルクリック動作。即時反映。</summary>
    public bool BoardListOpenOnSingleClick { get; init; } = true;

    /// <summary>スレッド一覧ペインで 1 クリックで開く。OFF でダブルクリック動作。即時反映。</summary>
    public bool ThreadListOpenOnSingleClick { get; init; } = true;

    // ---- タブ動作 (Phase 11c) ----
    // アクション識別子: "none" / "close" / "refresh" / "addFavorite" / "deleteLog"
    //                  / "closeOthers" / "closeLeft" / "closeRight"
    // addFavorite / deleteLog はスレッドタブでのみ有効。スレ一覧タブで指定しても無視される。

    /// <summary>スレ一覧タブの中クリック (ホイールクリック) アクション。</summary>
    public string ThreadListTabMiddleClickAction { get; init; } = "close";
    /// <summary>スレ一覧タブの Ctrl + 左クリックアクション。</summary>
    public string ThreadListTabCtrlClickAction   { get; init; } = "none";
    /// <summary>スレ一覧タブの Shift + 左クリックアクション。</summary>
    public string ThreadListTabShiftClickAction  { get; init; } = "none";
    /// <summary>スレ一覧タブの Alt + 左クリックアクション。</summary>
    public string ThreadListTabAltClickAction    { get; init; } = "none";
    /// <summary>スレ一覧タブの左ダブルクリックアクション。</summary>
    public string ThreadListTabDoubleClickAction { get; init; } = "refresh";

    /// <summary>スレッドタブの中クリック (ホイールクリック) アクション。</summary>
    public string ThreadTabMiddleClickAction { get; init; } = "close";
    /// <summary>スレッドタブの Ctrl + 左クリックアクション。</summary>
    public string ThreadTabCtrlClickAction   { get; init; } = "none";
    /// <summary>スレッドタブの Shift + 左クリックアクション。</summary>
    public string ThreadTabShiftClickAction  { get; init; } = "none";
    /// <summary>スレッドタブの Alt + 左クリックアクション。</summary>
    public string ThreadTabAltClickAction    { get; init; } = "none";
    /// <summary>スレッドタブの左ダブルクリックアクション。</summary>
    public string ThreadTabDoubleClickAction { get; init; } = "refresh";
}
