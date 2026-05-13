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

    /// <summary>書き込み成功時に <c>data/kakikomi.txt</c> へ Jane Xeno 形式で追記するか。
    /// 既定 ON。OFF にすると追記を行わない (既存ファイルには触らない)。即時反映。</summary>
    public bool EnableKakikomiLog { get; init; } = true;

    /// <summary>アプリ終了時に開いていたタブ (スレ一覧タブ + スレタブ両方) を
    /// <c>data/app/open_tabs.json</c> に保存し、次回起動時にその順番のまま自動で開き直すか。既定 ON。
    /// OFF にしても保存ファイルは作られる (= 後で ON に戻したら復元可)、復元処理だけがスキップされる。
    /// 即時反映 — 設定変更タイミングではなく次回起動・終了で効く。</summary>
    public bool RestoreOpenTabsOnStartup { get; init; } = true;

    // ---- 通信 ----
    /// <summary>カスタム User-Agent。空文字なら既定 (Monazilla/1.00 ChBrowser/&lt;ver&gt;)。即時反映。</summary>
    public string UserAgentOverride { get; init; } = "";

    /// <summary>HTTP タイムアウト (秒)。次回起動時に反映 (HttpClient.Timeout は実行中変更不可のため)。</summary>
    public int TimeoutSec { get; init; } = 30;

    // ---- 認証 (どんぐりメール認証) ----
    /// <summary>どんぐり (5ch) のメール認証アドレス。空文字なら認証無し (= 通常の acorn だけで投稿)。
    /// 設定がある状態でアプリ起動するとバックグラウンドでログインを試行する。</summary>
    public string DonguriEmail { get; init; } = "";

    /// <summary>どんぐりログインのパスワード (現在は平文で config.json に保存)。
    /// セキュア保管 (DPAPI 等) は後続で検討。空ならログイン試行はスキップ。</summary>
    public string DonguriPassword { get; init; } = "";

    // ---- スレッド ----
    /// <summary>人気レス閾値 (= 被アンカー数 ≥ この値で赤マーカー)。即時反映 (setConfig メッセージ)。</summary>
    public int PopularThreshold { get; init; } = 3;

    /// <summary>新規スレタブ生成時のデフォルト表示モード (Flat / Tree / DedupTree)。</summary>
    public string DefaultThreadViewMode { get; init; } = "DedupTree";

    /// <summary>画像 HEAD 自動取得しきい値 (MB)。これ以上の画像はクリックされるまで取得しない。即時反映。</summary>
    public int ImageSizeThresholdMb { get; init; } = 5;

    /// <summary>同一 ID の書込が何件以上あったら ID の "ID" 文字列を赤色で強調するかの閾値 (Phase 22)。
    /// 既定 5 = 同一 ID が 5 件以上で赤。1 にすると同 ID が 2 件以上で赤、等。
    /// 同 ID が 2 件以上 (= 単に複数) のときはリンク風装飾は常に出る (赤化はこの閾値を超えたときだけ)。
    /// ワッチョイには赤化の閾値は無く、複数あればリンク風装飾のみ。即時反映 (setConfig メッセージ)。</summary>
    public int IdHighlightThreshold { get; init; } = 5;

    /// <summary>ワッチョイ / ID のリンク風装飾に対するポップアップ表示モード。
    /// true (既定): クリックして初めて表示 (誤ホバーで邪魔にならない)。
    /// false: 従来通りマウスホバーで表示。
    /// 即時反映 (setConfig メッセージ → thread.js)。</summary>
    public bool MetaPopupClickOnly { get; init; } = true;

    // ---- 画像 ----
    /// <summary>画像キャッシュ上限 (MB)。即時反映 (ImageCacheService.MaxBytes)。</summary>
    public int CacheMaxMb { get; init; } = 1024;

    // ---- ビューア ----
    /// <summary>ビューアウィンドウのタブサムネイルサイズ (px、正方形)。即時反映 (ImageViewerWindow XAML binding)。</summary>
    public int ViewerThumbnailSize { get; init; } = 80;

    /// <summary>ビューアウィンドウを開いたとき、画像詳細ペイン (右側) をデフォルトで開いた状態にするか。
    /// false なら閉じた状態 (アコーディオン帯だけ) で起動する。Tab / 帯クリックでいつでも開閉可。
    /// 反映タイミング: ビューアウィンドウの初回生成時。既に開いている viewer は影響を受けない。</summary>
    public bool ViewerDetailsPaneDefaultOpen { get; init; } = true;

    // ---- ペイン操作 (Phase 11b) ----
    /// <summary>お気に入りペインで 1 クリックで開く。OFF でダブルクリック動作。即時反映 (favorites.js への setConfig)。</summary>
    public bool FavoritesOpenOnSingleClick { get; init; } = true;

    /// <summary>バッチ処理 (お気に入りチェック等) で subject.txt / dat の通信を行う際の同時実行本数。
    /// ユーザが直接トリガーする操作 (タブクリックでスレを開く等) はこの制限の対象外。
    /// 1 で完全直列、上げると速くなるが帯域占有。範囲: 1〜50 を想定。即時反映。</summary>
    public int BatchConcurrency { get; init; } = 6;

    /// <summary>板一覧ペインで 1 クリックで開く。OFF でダブルクリック動作。即時反映。</summary>
    public bool BoardListOpenOnSingleClick { get; init; } = true;

    /// <summary>スレッド一覧ペインで 1 クリックで開く。OFF でダブルクリック動作。即時反映。</summary>
    public bool ThreadListOpenOnSingleClick { get; init; } = true;

    // ---- タブ表示 (= 設定画面「タブ」カテゴリ) ----
    // 旧 click action 設定 (Middle/Ctrl/Shift/Alt/DoubleClick) はショートカット & ジェスチャー
    // 設定ウィンドウ側に移設済み (= スレ一覧のタブ領域 / スレッドタブ表示領域 カテゴリ)。
    // タブ幅の WidthMode: "chars" (文字数) / "px" (ピクセル)。

    /// <summary>スレ一覧タブの幅指定モード。"chars" = 文字数指定 / "px" = ピクセル指定。</summary>
    public string ThreadListTabWidthMode { get; init; } = "chars";
    /// <summary>スレ一覧タブの幅 (文字数)。WidthMode=chars のときに有効。</summary>
    public int    ThreadListTabWidthChars { get; init; } = 15;
    /// <summary>スレ一覧タブの幅 (px)。WidthMode=px のときに有効。</summary>
    public int    ThreadListTabWidthPx    { get; init; } = 200;

    /// <summary>スレッドタブの幅指定モード。"chars" = 文字数指定 / "px" = ピクセル指定。</summary>
    public string ThreadTabWidthMode { get; init; } = "chars";
    /// <summary>スレッドタブの幅 (文字数)。WidthMode=chars のときに有効。</summary>
    public int    ThreadTabWidthChars { get; init; } = 15;
    /// <summary>スレッドタブの幅 (px)。WidthMode=px のときに有効。</summary>
    public int    ThreadTabWidthPx    { get; init; } = 200;
}
