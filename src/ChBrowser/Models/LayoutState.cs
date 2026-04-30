namespace ChBrowser.Models;

/// <summary>
/// ウィンドウ位置・サイズ + 主要ペインの寸法を保存する永続化用レコード。
/// data/app/layout.json として直列化する。
///
/// <para>
/// 保存対象は伸縮可能な「左ペイン (板一覧) の幅」と「右上ペイン (スレ一覧) の高さ」だけ。
/// 残りの ColRight (スレ表示+ツールバー) と RowThreadView は Star サイズ (`*`) で残スペースを
/// 自動的に吸うため明示保存不要。
/// </para>
///
/// <para>
/// <see cref="WindowMaximized"/> が true の時、Width/Height/Left/Top には RestoreBounds (= 通常表示時の
/// サイズ・位置) を入れて保存する。次回起動時にまず通常サイズで復元してから最大化する。
/// </para>
/// </summary>
public sealed record LayoutState(
    double WindowLeft,
    double WindowTop,
    double WindowWidth,
    double WindowHeight,
    bool   WindowMaximized,
    double BoardListWidth,
    double ThreadListHeight,
    /// <summary>左ペイン上の「お気に入り」ペインの高さ。Phase 7 で追加。
    /// 0 / NaN なら保存無し扱いで XAML 既定値を使う。</summary>
    double FavoritesPaneHeight = 0);
